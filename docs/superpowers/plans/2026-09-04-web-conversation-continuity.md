# Resumable Web Workspace and Conversation Continuity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #35 by giving the signed-in web channel a responsive conversation-primary layout with an accessible live Inventory workspace, a finite resumable per-Turn Server-Sent Events stream carrying progress, semantic response parts and one terminal Outcome with event IDs, a separate Participant-level stream that invalidates Inventory projections after a change from any channel, one browser-profile ChannelConversation that resumes across refreshes/restarts/tabs while preserving the shared FIFO queue, disconnect recovery that never resubmits unknown mutation-capable work, an explicit-only Active Inventory switch, and a "New conversation" action that atomically rotates the Foundry conversation generation and clears pending conversational confirmation state without removing any authorized access.

**Architecture:** The per-Turn stream is a **projection with a minimal durable progress log**, read through one deep Application seam (`TurnEventReader`). Three of the four event kinds are projected from state that is already durable and permanent - `accepted` from the `InboxEntries` row, the semantic `part` events and the terminal `outcome` from the `Outcomes` row and its `Deliveries` - so they replay identically after any process restart and carry no second copy of a short-lived confirmation token. Because the `part` events are projected from the recorded `Outcomes` row, they become readable at the instant the Turn completes and therefore arrive **together with** the terminal event; `accepted` and `processing` are the incremental signals a Participant sees while waiting (see Known limits). The one event that durable state cannot express, `processing`, is a real row in a new bounded `TurnProgressEvents` table with a 24-hour delete sweep. Every event ID is a **fixed constant sequence** (`accepted`=1, `processing`=2, `part` n=99+n, `outcome`=1,000,000), which is what makes replay-after-`Last-Event-ID` exact, makes appends idempotent by primary key with no counter read and therefore no race, and makes a swept log indistinguishable from a full one. Inventory invalidation uses a per-Inventory `InventoryVersions.Version` counter bumped **inside the same transaction as every mutation**, from one seam - a `MultiChannelAgentDbContext.SaveChangesAsync` override that keys off the `InventoryAuditEntity` rows every state-changing store already stages - so no endpoint, worker, or future channel can forget to publish, and nothing is ever published before commit. The Participant-level stream is deliberately **snapshot-and-diff rather than cursor-replay**: every connection opens with the complete current version of every authorized Inventory, which makes any reconnect a total resynchronization and removes the identity-gap, retention, and membership-drift failure modes a cursor would introduce. Conversation rotation is one guarded, transactional store operation, and old work can never enter new history because each Turn **captures its Foundry conversation and generation at acceptance** on its own inbox row - and a Turn whose captured generation is no longer the conversation's current one is recognized as **accepted in a superseded conversation**, so any confirmation it would otherwise have left waiting is settled in the very pass that created it and can never become confirmable in the new conversation. That recognition is deliberately made **twice**: once before the Turn runs, from the trusted context, and once again after tool dispatch by **re-reading the conversation's current generation**, because a reset is free to commit while a Turn is being processed and an answer computed before it cannot know that.

**Tech Stack:** C#/.NET 10, EF Core 10 (SQL Server in production, SQLite for Docker-free relational tests), ASP.NET Core minimal APIs with a custom `IResult` for `text/event-stream`, xUnit 2.9 with plain `Assert`, `Xunit.SkippableFact`, `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers `MsSql`, React 19 + TypeScript + Vite 8 + oxlint, and (new) Vitest 5 + Testing Library + jsdom for web runtime tests.

---

## Scope and non-goals

In scope - issue #35's acceptance criteria, verbatim:

1. Desktop and narrow-screen layouts keep conversation primary and expose an accessible live Inventory workspace.
2. Typed Turns return stable Turn IDs and publish finite resumable SSE progress, semantic parts, and terminal Outcomes with event IDs.
3. A separate Participant-level SSE stream invalidates Inventory projections after changes from any channel.
4. One browser-profile ChannelConversation resumes across refreshes, restarts, and tabs while preserving the shared FIFO queue.
5. Disconnect recovery retrieves recorded status and Outcome without resubmitting unknown mutation-capable work.
6. "Use in this conversation" explicitly switches Active Inventory and records the switch; browsing never switches implicitly.
7. "New conversation" rotates Foundry history and clears pending clarification/confirmation state without removing authorized access.

Explicitly **out of scope**:

- **Monetary budgets, spend thresholds, chargeback, cost ceilings, cost-triggered shutdown, and quota purchase.** Parent #26 puts every one of these out of scope, and #35 restates it. **No task in this plan may add a cost check, a spend ceiling, or a budget policy of any kind.**
- Voice, WebRTC, Azure Voice Live, barge-in, and speech-to-speech. #26 lists those separately; #35 is text only.
- Teams and email channels. This ticket only touches the `web` channel adapter surface.
- A real Foundry-backed `IModelBoundary`. `ScriptedModelBoundary` remains the production implementation; rotation rotates the durable **binding** (identity + generation), which is what a later Foundry integration will read.
- Raw model tokens on the wire. #26: *"The core emits typed progress/status events, channel-neutral response parts, and one terminal Outcome. It does not expose raw model tokens."* No task may stream tokens.
- Any second stock-mutation path. The Inventory workspace stays an authoritative **read** projection; no task may add a quantity input, a save button, or any direct mutation control to it.
- Clearing Initial Import proposals on "New conversation". An `ImportProposal` is keyed by (Participant, Inventory) and belongs to a browser file workflow, not to a ChannelConversation; #26 says rotation clears *pending clarification/confirmation*, which in this system is exactly the one `ConfirmationProposal` per (Participant, ChannelConversation). Task 12 asserts the import proposal survives.
- Persisting any Outcome payload in the browser. `turnsApi.ts` already documents the `stock_proposal`/`reference_proposal` `token` as a short-lived secret that must not be persisted separately; Task 17 persists only a Turn ID and a native message ID.
- Playwright or any browser-driving end-to-end runner. Task 15 justifies the minimal jsdom-based runtime test tooling instead.

---

## Deliberate design decisions

These were the open questions. Each is decided here, once, with its reason. No task may silently choose differently.

### D1. The per-Turn event stream is a projection plus a minimal durable progress log - not a full event log

**Considered:** (a) synthesize the whole stream from `InboxEntries` + `Outcomes`; (b) a full durable per-Turn event log written atomically with every state change.

**Decided:** a hybrid. `accepted`, `part`, and `outcome` are **projected** from the permanent `InboxEntries` and `Outcomes`/`Deliveries` rows. `processing` is a **durable row** in a new `TurnProgressEvents` table.

**Why:** Option (a) cannot express progress at all - `InboxEntries.Status` stays `Pending` from acceptance until completion, so "accepted but not started" and "accepted and being worked on" are indistinguishable, and AC 2 explicitly requires progress. Option (b) forces `ITurnResultStore.RecordAsync` to grow a third atomic effect and, worse, writes a **second copy of the Outcome payload** - which for a `stock_proposal` or `reference_proposal` contains a plaintext single-use confirmation token with its own ten-minute retention. Two copies of a short-lived secret with two retention paths is a defect waiting to happen. Projecting the parts from `Outcomes.Payload` means the shipped `OutcomePayloadCleanupCoordinator` already expires the streamed copy at exactly the right instant, because there is only one copy. The hybrid keeps `ITurnResultStore`'s contract untouched, adds one small bounded table, and still survives process restart and replays exactly after `Last-Event-ID` because the projected sources are permanent.

**What this costs, stated plainly:** because the parts are projected from the recorded `Outcomes` row, they do not exist until the Turn completes. The stream is therefore incremental in its *status* events (`accepted`, then `processing`) and batched in its *content* events (`part`, `part`, `outcome`, all readable in the same poll). That is what AC 2's "progress, semantic parts, and terminal Outcomes" means here, and it is repeated in Known limits so nobody reads "streaming" as "token-by-token" - which #26 forbids anyway.

### D2. Event IDs are fixed constant sequences, not an incrementing counter

**Decided:** `accepted` = 1, `processing` = 2, `part` with 1-based order n = `99 + n` (bounded to 64 parts, so 100..163), `outcome` = 1,000,000.

**Why:** three properties fall out for free. (1) An append is idempotent by primary key `(TurnId, Sequence)` with **no counter read**, so it has none of the read-then-write contention `SqlInboxStore.AcceptAsync` needs a bounded retry loop for. (2) A swept progress row and a never-written one are indistinguishable to the reader, so retention needs no special case. (3) The terminal sequence is a compile-time constant, so `Last-Event-ID: 1000000` is answerable ("you already have everything") without reading anything. Gaps in SSE `id` values are explicitly fine: the protocol only needs the server to understand its own IDs.

### D3. Retention of the progress log matches the Outcome payload's own window

**Decided:** `TurnProgressEvent.Retention = 24 hours`, identical to `Outcome.PayloadRetention`, swept by a new leased `TurnProgressEventCleanupCoordinator` + `TurnProgressEventCleanupWorker` that mirror `OutcomePayloadCleanupCoordinator`/`OutcomePayloadCleanupWorker` exactly (15-minute period, 500-row batches, own lease).

**Why:** a progress marker is worthless once the answer is permanent, and the Outcome payload is the thing a reconnecting Participant is actually retrieving. Using one number for both means an operator never has to reason about two windows. The `Outcomes` row itself stays permanent, so a Turn never stops having an answer - exactly as today.

### D4. SSE endpoint mechanics

- **Route:** `GET /api/turns/{turnId:guid}/events`, in the existing `/api/turns` group, therefore already behind `AuthorizationPolicies.ActiveTenantMember`. No CSRF filter (it is a GET with no side effects).
- **Ownership / non-disclosure:** `TurnEventReader.ReadAfterAsync` returns `null` for both an unknown Turn and another Participant's Turn, and the endpoint answers a plain `404` for both - the identical shape `GET /api/turns/{id}/outcome` already uses.
- **Headers:** `Content-Type: text/event-stream`, `Cache-Control: no-cache, no-store`, `X-Accel-Buffering: no`, and `IHttpResponseBodyFeature.DisableBuffering()`.
- **Framing:** `id: <sequence>\nevent: <name>\ndata: <single-line JSON>\n\n`. `JsonSerializer` escapes newlines inside strings and never pretty-prints, so `data` is always exactly one line; Tasks 3 and 6 assert it.
- **`Last-Event-ID`:** read from the `Last-Event-ID` request header first, then the `lastEventId` query string (a browser only sends the header on its *own* automatic reconnect; a fresh `EventSource` after a page reload cannot set headers at all). A value that does not parse, is negative, or is not a sequence this application issues is **ignored and the stream replays from the beginning** - the same "anything else is treated exactly as if none had been sent" rule `WebConversationCookie` already applies. A `400` was rejected because `EventSource` cannot read an error body and would reconnect forever with the same bad value.
- **Finite completion:** once the terminal `outcome` event has been written the handler returns and the response ends. The client closes its `EventSource` on `outcome`, so no reconnect follows.
- **Interactive wait bound:** `MaxDuration = 5 minutes`. A stream that has not reached terminal by then ends without a terminal event; `EventSource` reconnects automatically with `Last-Event-ID` and resumes. This is user story 112's bounded interactive wait.
- **Heartbeat:** an SSE comment line (`: heartbeat\n\n`) every 15 seconds of silence. Necessary, not decorative: Azure Container Apps ingress closes idle connections well inside the 5-minute bound, and a comment carries no `id`, so it can never corrupt `Last-Event-ID`.
- **The three timings are injected, never hard-coded.** `TurnStreamOptions` (poll interval, heartbeat interval, interactive-wait bound) and `InventoryStreamOptions` are plain singletons registered in `Program.cs` holding exactly the production numbers stated here. This exists for one reason: proving the heartbeat fires is a real requirement, and a test that proved it by waiting fifteen real seconds would add fifteen seconds to every CI run forever. A `FakeTimeProvider` cannot serve instead - these handlers run inside a live HTTP request that a test is concurrently reading bytes from, so nobody is in a position to advance a fake clock at the right moment without racing the handler. Overriding one singleton with a 200 ms heartbeat is deterministic, needs no clock control, and leaves production untouched.
- **Polling:** the handler polls the reader every 500 ms **in a fresh DI scope per iteration** (`IServiceScopeFactory`, exactly like the hosted workers) so a five-minute request never holds one `DbContext` open. In-process notification was rejected because the Container App runs multiple replicas and a Turn can be processed by a different replica than the one holding the stream.
- **Disconnect:** `HttpContext.RequestAborted` cancels the loop and `OperationCanceledException` is swallowed. The endpoint only ever reads, so a disconnect can never undo, duplicate, or resubmit anything.
- **Recovery without resubmission:** reconnecting is a `GET`. The shipped `POST /api/turns` remains idempotent by `(ParticipantId, ChannelConversationId, NativeMessageId)` and already returns the recorded Outcome for a duplicate, so even the client's worst case - re-POSTing a native message id whose response was lost - can only ever converge on the one recorded Turn.

### D5. Inventory invalidation publishes from the persistence seam, and the Participant stream is snapshot-and-diff

**Considered for the durable source:** (a) reuse `InventoryAudits` rows; (b) a dedicated append-only event table with an IDENTITY sequence; (c) a per-Inventory version counter.

**Decided:** (c) a per-Inventory `InventoryVersions.Version`, bumped in the same transaction as the mutation by a `MultiChannelAgentDbContext.SaveChangesAsync`/`SaveChanges` override that keys off `Added` `InventoryAuditEntity` rows whose `EventType` is not `AccessDenied`.

**Why not (a):** `InventoryAudits.Id` is a GUID and `OccurredAtUtcTicks` is neither unique nor monotonic, so audit rows carry no total order; and audits are a 90-day security artifact whose retention rules should not be coupled to a UX refresh signal. **Why not (b):** an IDENTITY column is assigned at INSERT and becomes visible at COMMIT, so a reader can consume sequence 6 before sequence 5 commits and record a cursor that permanently skips it. That gap is not hypothetical under concurrent transactions and there is no portable fix. **Why (c) is safe:** the bump is `UPDATE InventoryVersions SET Version = Version + 1 WHERE InventoryId = @id` - atomic at the database, needing no read, so it cannot lose an update the way a read-then-write counter can - and it runs **after** `base.SaveChangesAsync` inside the same transaction.

**What running it last does and does not buy, precisely.** It buys **commit coupling**: the version becomes visible exactly when the change it announces commits, never before, and a rollback takes it with the change. It buys a **short lock hold**: the version row's exclusive lock is taken at the very end and released at commit, so it is held for the shortest possible slice of the transaction. It does **not** serialize the work that came earlier in those transactions, and it is **not** a deadlock-prevention scheme - two transactions that would already deadlock on Stock rows still can, exactly as they could before this table existed, and the engine still resolves that by choosing a victim. No writer is asked to take the version lock first, and nothing in this plan depends on one. `SqlReferenceAdministrationStore` and `SqlImportExecutionStore` keep their `Serializable` transactions unchanged, and the shipped concurrency tests that already tolerate SQL Server deadlock victims keep tolerating them.

**Why the version row has no foreign key to `Inventories`.** `InventoryAuditEntityConfiguration` says it outright for the fact this seam keys off: *"No foreign keys to Inventories/Participants: an audit row must remain a durable, minimal fact independent of later changes to (or eventual retirement of) either referenced row."* A cascading foreign key on `InventoryVersions` would contradict that in the one place it matters most - inside a mutating transaction. The bump's fallback insertion (for an Inventory somehow left without a row) would have to satisfy a foreign key while the audit fact that triggered it deliberately does not, so a state the audit model explicitly tolerates would become a hard failure of an unrelated write. `InventoryVersions` therefore mirrors `InventoryAudits`: keyed by the Inventory, indexed by it through that key, and referentially independent of it. Consistency comes from the two mechanisms that actually establish it - the migration backfills a row for every existing Inventory, and the save-time seam seeds one for every new Inventory in the same save - with the fallback insertion as the guarded third line of defence. Task 7 asserts all three, including that the created table carries no foreign key at all.

**Why the Participant stream is snapshot-and-diff, not cursor-replay:** invalidation is idempotent and state-based - the client needs the *current* version of each Inventory it displays, not the history of how it got there. Every connection therefore opens with a complete snapshot of every authorized Inventory's current version.

**Why that is genuinely resumable without event IDs, rather than merely convenient.** A resumable stream is one where a client that reconnects ends up knowing everything it would have known had it never disconnected. This stream satisfies that by construction, and for a reason that does not depend on a cursor: what a client needs to know is a *function of current state* (the version each authorized Inventory is at right now), not a function of the event history. A cursor is needed only when the events carry information that current state does not - a delta, an ordering, a payload that is discarded after delivery. Here they carry none: `changed` says nothing that the next snapshot does not say, and `revoked` says nothing that the next snapshot's absence does not say. A missed `changed` is therefore not a lost change; it is a change the client learns about one snapshot later, which is the same instant it would learn about it if the connection had merely been slow. Because `Last-Event-ID` could not improve on that, this stream deliberately emits **no `id:` lines**: an `id` would advertise cursor semantics the server does not implement, and a client that trusted it would be resuming from a position the server would silently ignore. Task 8 proves the claim rather than asserting it, with a test that disconnects, changes an Inventory while nothing is connected, reconnects, and finds the new version in the reconnect snapshot. Route: `GET /api/inventory-events`.

**Why the audit row is the right trigger:** every store that changes Inventory-visible state already stages an `InventoryAuditEntity` in the same `SaveChanges` - `SqlStockMutationStore`, `SqlStockChangeSetStore`, `SqlReferenceAdministrationStore`, `SqlImportExecutionStore`, `SqlInventoryMembershipStore`, `SqlInventoryOwnershipStore`, `SqlInventoryRecoveryStore`. Keying off it means a future mutation path cannot forget to publish without also forgetting to audit, which is a far louder failure. `AccessDenied` is excluded because it changes nothing. Inventory creation writes no audit, so the override also seeds a `Version = 0` row for every `Added` `InventoryEntity`; the stream reports a brand-new Inventory the first time it appears in the authorized set. One consequence has to be kept in mind by every test that counts versions: **granting or removing a Membership is an audited change**, so it bumps the version of the Inventory it happened in. Tests therefore assert against a captured baseline rather than against an assumed zero.

### D6. Rotation captures the Foundry generation at acceptance and therefore never has to reject

**Considered:** (a) refuse to rotate while any Turn in the conversation is still `Pending`; (b) stamp each Turn with the Foundry conversation and generation it was accepted under.

**Decided:** (b). `InboxEntries` gains nullable `FoundryConversationId` and `FoundryConversationGeneration` columns, written by `TurnAcceptanceService` at acceptance; `TurnExecutionContextFactory` reads them back and uses them, rather than using the *current* binding, to decide which conversation a Turn belongs to at processing time. (From D10 onwards it also reads the current binding - but only to notice that a reset happened in between, never to decide where the Turn belongs.)

**Why not (a):** the Participant who most needs "New conversation" is the one whose conversation is stuck, and a stuck head Turn already blocks that conversation's FIFO. Refusing to reset exactly then is the wrong answer, and making the check race-free would require a `Serializable` range lock over `InboxEntries` on a hot path. **Why (b) is race-free:** an acceptance either reads generation *n* before rotation commits, or *n+1* after; either way the Turn is stamped with a generation that genuinely existed, its old Foundry conversation identity lives on its own inbox row so it is never lost, and work accepted before a reset can never enter the new history. There is no interleaving that corrupts anything. FIFO is untouched: a Turn accepted before rotation still queues ahead of one accepted after, which is exactly AC 4's "preserving the shared FIFO queue".

**Rotation atomicity:** one `SqlConversationRotationStore.RotateAsync` transaction does a guarded `WHERE Generation = @expected` update of the binding (so two concurrent rotations produce exactly one increment - the loser re-reads and retries, bounded, exactly like `SqlInboxStore.AcceptAsync`) and, in the same transaction, settles the one pending `ConfirmationProposal` for that (Participant, ChannelConversation) to a new `ProposalStatus.ConversationReset`. `Memberships` and `ActiveInventorySelections` are never touched, which is how AC 7's "without removing authorized access" is guaranteed structurally rather than by remembering not to.

**What this decision does not cover, and D10 does:** settling at rotation time settles what is pending *at that moment*. A mutation-capable Turn accepted under the old generation but still queued can be processed *after* the rotation commits and would then create a brand-new `ConfirmationProposal` - one the rotation never saw and therefore never settled. D10 closes that.

### D7. Cross-tab and restart continuity without exposing any token

**Decided:** authentication stays entirely server-side in the existing HttpOnly Secure cookies; the browser persists **only** `{ turnId, nativeMessageId }` for the one in-flight Turn, in `localStorage`, keyed by the `webConversationId` the bootstrap already returns. Cross-tab coordination uses the DOM `storage` event. No `BroadcastChannel`.

**Why:** `localStorage` is scoped per origin per browser profile - exactly the scope `WebConversationCookie`'s 400-day cookie already has - and its `storage` event fires in *other* tabs by specification, so the one mechanism that is required for persistence also provides the cross-tab signal with no second API and no feature detection. The Outcome is deliberately never persisted: `turnsApi.ts` documents the proposal `token` inside it as a short-lived secret that must not be persisted separately, and it does not need to be, because reconnecting the stream replays it from the server. On load, a stored `turnId` reconnects its stream (a pure `GET`); a stored `nativeMessageId` with no `turnId` - the case where the original POST's response was lost - re-POSTs **the same** native message id, which the shipped idempotency contract turns into "return the one recorded Turn", never a second mutation.

### D8. Responsive, accessible layout

**Decided:** conversation is `<main>` and comes first in DOM order at every width. At >= 1024 px the workspace is an `<aside aria-label="Inventory workspace">` in a two-column CSS grid with the conversation column wider. Below 1024 px both live in a single column behind an ARIA tab list (`role="tablist"`/`role="tab"`/`role="tabpanel"`, `aria-selected`, `aria-controls`, roving `tabindex`, Left/Right/Home/End keys), with **Conversation** selected by default. **The `main` landmark survives the narrow layout**: the tab list and the one rendered `tabpanel` live *inside* `<main>` rather than `role="tabpanel"` being put on the `<main>` element itself. An explicit `role` replaces an element's implicit one, so `<main role="tabpanel">` would delete the page's only main landmark at exactly the widths where landmark navigation matters most - and would leave the page with no `main` at all for a screen-reader user skipping to content. The active Inventory is shown in the always-visible `<header>` so an explicit switch never scrolls out of sight. The narrow-screen breakpoint is read with `window.matchMedia`, which jsdom tests stub.

**Why:** #26 says "Make conversation primary with a collapsible Inventory panel or narrow-screen tab/sheet". DOM order decides both reading order for assistive technology and the default focus order, so making conversation first is what actually makes it primary - CSS ordering alone would not.

### D9. Minimal web runtime test tooling

**Decided:** add Vitest 5, `@testing-library/react`, `@testing-library/user-event`, `@testing-library/jest-dom`, and `jsdom` as devDependencies, with `"test": "vitest run"` and one new CI step. No Playwright.

**Why:** almost every acceptance criterion in #35 is *client runtime behaviour* - reconnect, resume, cross-tab, tab navigation, live regions - and the repository currently has no way to assert any of it, which would leave those criteria verified only by hand. Vitest reuses the existing Vite config and transform pipeline, so no second build system enters the repository. Playwright was rejected: it needs browser downloads in CI and would only add value for true visual layout, which this plan asserts as CSS, not as behaviour. `EventSource` does not exist in jsdom, so the stream clients take an injectable factory rather than depending on a polyfill.

### D10. A Turn accepted in a superseded conversation can never leave a confirmable proposal

**The hole D6 leaves.** D6 stamps each Turn with the generation it was accepted under, and rotation settles whatever confirmation was pending when it ran. Neither covers this interleaving: a mutation-capable Turn ("forget stock Steel Bolts") is accepted under generation *n*; the Participant clicks "New conversation", which commits generation *n+1* and settles nothing, because at that instant nothing is pending; the queued Turn is then processed and stores a **new** `ConfirmationProposal` for that (Participant, ChannelConversation). AC 7 promises a reset clears pending confirmation state, and that promise would be broken by a proposal created after the reset out of work from before it.

**Considered:** (a) stamp the captured generation onto `ConfirmationProposal` as its own column and compare it at confirmation time; (b) refuse to process any Turn whose captured generation is stale; (c) detect at processing time that the Turn belongs to a superseded conversation, and settle anything it leaves pending in the same pass.

**Decided:** (c), with the detection made **twice** - once in `TurnExecutionContextFactory`, before the Turn runs, and once again inside `ConfirmationProposalLifecycle` after it has run, by re-reading the binding rather than trusting the earlier answer.

**Why not (a):** it copies a fact that is already durable. `InboxEntries.FoundryConversationGeneration` (D6) records the generation a Turn was accepted under, and the trusted `TurnExecutionContext` already carries it, so a column on the proposal would be a second copy of a fact the processing path already holds - with a migration, a backfill, a nullable legacy case, and the standing possibility of the two disagreeing. **Why not (b):** a stale *read* ("list stock") must still complete, against the history it was accepted into; #26 and AC 4 both require a reset not to abandon accepted work, and refusing would abandon it. **Why (c) does not destroy legitimate work:** per-ChannelConversation FIFO. `IInboxStore.ClaimPendingAsync` only ever offers a conversation's head, so every Turn accepted before a rotation is processed before every Turn accepted after it. A superseded-generation Turn can therefore never be processed *after* a current-generation Turn in the same conversation, which means settling on supersession can never destroy a proposal a newer Turn had just made - there cannot be one yet.

**Why the answer must be recomputed after dispatch, not reused.** A single detection made when the trusted context is assembled is a **snapshot of a value that keeps changing while the Turn runs**. Rotation is not quiesced against processing: nothing stops `SqlConversationRotationStore.RotateAsync` from committing after the context was built and before the tool dispatcher stores its proposal. In exactly that interleaving a flag captured at context-creation time reads `false`, the Turn stores a fresh `ConfirmationProposal`, and a settle gated on that flag does nothing - leaving a confirmable proposal in the conversation the Participant just started, which is the defect AC 7 forbids. So `SettleSupersededConversationAsync` **re-reads the conversation's current binding through `IFoundryConversationBindingStore` at settlement time** and compares its generation with `TurnExecutionContext.FoundryConversationGeneration` - the generation this Turn was accepted under, which is durable on its own inbox row (D6) and therefore cannot go stale. `TurnExecutionContext.AcceptedInSupersededConversation` survives, but only for the pre-dispatch reconcile, where it *is* fresh because it was computed moments earlier in the same pass.

**How it is enforced: one flag for the pre-dispatch pass, one re-read for the post-dispatch pass.**

1. **Detection, for work that already existed.** `TurnExecutionContextFactory` reads the Turn's captured binding (D6) *and* the conversation's current binding, and sets `TurnExecutionContext.AcceptedInSupersededConversation` when the generations differ. The captured binding still decides which Foundry conversation the Turn continues; the current one is read only to answer this question.
2. **Before the Turn does anything.** `ConfirmationProposalLifecycle.ReconcileAsync` gains one case: a superseded Turn settles whatever was already pending as `ConversationReset`, before the model is asked anything. This covers a proposal that outlived the rotation for any reason.
3. **After the Turn has done everything.** `ConfirmationProposalLifecycle.SettleSupersededConversationAsync`, which `TurnProcessingCoordinator` calls **after** tool dispatch and **before** `ITurnResultStore.RecordAsync`, re-reads the current binding and settles anything the Turn itself just stored when the generation has moved past the one it was accepted under. This is the case D6 could not see.

**The re-read must serialize with the rotation, or it is not a check at all.** Azure SQL databases are created with `READ_COMMITTED_SNAPSHOT` on, so an ordinary read is served from row versions. `RotateAsync` bumps the generation and settles pending proposals inside one transaction; while it is between its settle statement and its commit, an unhinted read of the binding answers with the generation that rotation is *replacing*. That is the interleaving **U < P < S < R** (rotation's settle, then the proposal, then the re-read, then the commit): the rotation settled nothing because the proposal did not exist yet, and the re-read reports the conversation as current. Neither mechanism fires and a confirmable proposal survives - on the production provider only, invisibly to a test database without the setting.

So the re-read goes through its own seam, `IFoundryConversationBindingStore.ReadCurrentForSupersessionAsync`, whose contract is to serialize against an in-flight rotation of that binding row: on SQL Server a parameterized `WITH (UPDLOCK)` read in a short explicit transaction, on SQLite an ordinary read under a storage engine that admits one writer. `GetOrCreateAsync` keeps its cheap semantics unchanged - every other caller wants the current answer, not a queue behind a reset - and the supersession seam never creates a binding, answering null (read as "still current", since a conversation with no binding has never been rotated) instead.

**The orderings, and why together they are exhaustive.** Write *P* for the instant the dispatcher's proposal becomes durable, *S* for the post-dispatch re-read, and *R* for the instant `RotateAsync` commits. *P* always precedes *S*, because *S* runs after dispatch returns. Because *S* and the rotation's generation bump both need conflicting locks on the same row, they are strictly ordered rather than passing through each other, which is what makes the case analysis below total rather than merely likely. Rotation can land anywhere, which gives three cases - the first two are the ones this task owns, and they are the two the review calls out:

- **R before the Turn is claimed (rotation first).** The factory sees captured *n* against current *n+1*, so `ReconcileAsync` settles anything already pending before the model is asked anything, and *S* re-reads *n+1* against the Turn's *n* and settles whatever the Turn itself stored, if anything. Both passes fire; nothing survives.
- **R between context creation and S (rotation during processing, model call, or dispatch).** The flag is `false` and `ReconcileAsync` correctly does nothing - at that moment the conversation genuinely was current. Then *P* happens, then *S* re-reads and finds *n+1* against *n*, and settles. **This is the ordering a captured bool misses**, and it is the entire reason for the re-read.
- **R at or after S (rotation lands between the settle and the Outcome being recorded, or later).** *S* is a no-op, because the generations still match - and it reached that answer by taking the binding row's lock first, so the rotation's generation bump, and therefore its settle statement, both run after *S* finished, which is after *P*. The proposal is durable and `Pending` by then, so `RotateAsync`'s own transaction - which settles the one pending `ConfirmationProposal` for that (Participant, ChannelConversation) as `ConversationReset` in the same transaction as the generation bump (D6) - settles it. Task 10 owns this case, not Task 11. Without the locking read this case would be a lie: *S* could answer "still current" from a row version while the rotation had *already* run its settle, and nothing would settle the proposal.

Every rotation is therefore either before *S* (caught by the re-read, because *P* < *S* means the proposal already exists to be settled) or at/after *S* (caught by rotation's own settle, for the same reason). **Stated honestly: this is a union of two mechanisms, not one atomic check.** The re-read and the proposal write are not in one transaction, and this plan deliberately does not put them in one - `ITurnResultStore.RecordAsync`'s atomic contract is untouched, and no store grows a new transactional effect. What makes the union total is only that the proposal is durable before either mechanism can miss it. A double settle is harmless and expected in the narrow overlap: `InvalidatePendingAsync` moves 0 rows the second time, which both call sites already treat as "nothing to settle".

**What the Participant sees:** the stale Turn still answers, and when the work it proposed has stopped being confirmable, the answer *says so*. `TurnProcessingCoordinator` replaces a `confirmation_required` decision with `ConfirmationProposalLifecycle.ConversationResetAnswer` - a `conflict` / `conversation_reset` answer carrying no payload and therefore no token - whenever the post-dispatch pass finds the conversation superseded. This is a decision about **what to record**, taken before `ITurnResultStore.RecordAsync` is called, so that contract is still untouched and the Outcome is still written exactly once, atomically, from a single decision.

**Why the answer is rewritten rather than recorded as decided.** The alternative - record `confirmation_required` with its token, and let the Participant discover on `confirm <code>` that there is nothing to confirm - hands back a bearer secret that is already spent. A client resuming that Turn's stream renders a confirmation prompt that can never succeed, and the Participant is told "there is nothing to confirm" only after acting on what they were shown. Both readings are honest about the ordering; only one of them is honest *at the time the answer is given*. The rewrite is deliberately narrow: it applies **only** when the decision's category is `ConfirmationRequired` **and** the conversation was found superseded, so it can never reshape an answer that had nothing to do with confirmation.

**Read-only outcomes are unchanged in every ordering.** A Turn that proposed nothing has nothing to settle and nothing to rewrite: it completes normally, with its own category, code, payload, and deliveries, against the history it was accepted into. Nothing here refuses, retries, or reshapes such a Turn.

**The superseded verdict, not the settle count, decides the answer.** The post-dispatch pass reports two separate facts - whether the conversation had been left behind, and what (if anything) it settled - because a reset that committed before the Turn's proposal was stored settles that proposal in its own transaction, leaving this pass nothing to settle. Keying the rewrite on "I settled something" would hand back a live-looking token in exactly that ordering; keying it on "the conversation moved on" does not.

No migration, no new column, and no change to `IConfirmationProposalStore`, whose existing `InvalidatePendingAsync` is exactly this operation. The one structural cost is that `ConfirmationProposalLifecycle` gains a second dependency, `IFoundryConversationBindingStore` - already registered, already scoped, and the only seam that can answer "what generation is this conversation on *now*".

**Initial Import is untouched.** `InvalidatePendingAsync` operates on `ConfirmationProposals` keyed by (Participant, ChannelConversation). An `ImportProposal` is a different table keyed by (Participant, Inventory) and is not reachable from here - the same structural reason Task 12 gives for rotation itself.

---

## File responsibility map

### Domain (`src/MultiChannelAgent.Domain/`)

| File | Responsibility |
| --- | --- |
| `Turns/TurnStreamEvent.cs` (create) | The stream vocabulary as data: `TurnEventKind`, `TurnResponsePartKind`, their machine text, `TurnEventSequence` (the fixed constants, `ForPart`, `IsIssued`), and `TurnProgressEvent` - the one durable progress record with its retention. |
| `Inventories/ConfirmationProposal.cs` (modify) | `ProposalStatus` gains `ConversationReset`. |

### Application (`src/MultiChannelAgent.Application/`)

| File | Responsibility |
| --- | --- |
| `Turns/ITurnProgressEventStore.cs` (create) | The durable progress seam: idempotent append by `(TurnId, Sequence)`, read all for a Turn, bounded expiry delete. |
| `Turns/TurnEventReader.cs` (create) | **The deep seam.** The single authority on what one Turn's event stream *is*: ownership/non-disclosure, projection of `accepted`/`part`/`outcome` from permanent state, replay of the durable `processing` row, `Last-Event-ID` filtering, terminal detection, and JSON serialization of every event's `data`. |
| `Turns/TurnProgressEventCleanupCoordinator.cs` (create) | Leased, bounded sweep of expired progress rows. |
| `Turns/TurnProcessingCoordinator.cs` (modify) | Appends the `processing` progress event before asking the model anything, and settles any confirmation a superseded-generation Turn leaves behind before recording its Outcome. |
| `Turns/TurnAcceptanceService.cs` (modify) | Resolves the Foundry binding at acceptance and passes it to the inbox so the Turn captures it. |
| `Turns/IInboxStore.cs` (modify) | `AcceptAsync` takes the captured `FoundryConversationBinding`; new `FindCapturedBindingAsync`. |
| `Turns/TurnExecutionContext.cs` (modify) | `TurnExecutionContextFactory` uses the captured binding (falling back to the current one only for Turns accepted before the migration) and flags `AcceptedInSupersededConversation` when that captured generation is no longer current. |
| `Inventories/ConfirmationProposalLifecycle.cs` (modify) | Adds the superseded-conversation invalidation, and the post-dispatch `SettleSupersededConversationAsync` that re-reads the conversation's current Foundry generation through `IFoundryConversationBindingStore` - so a reset that commits *while* a Turn is being processed still stops it leaving a confirmable proposal. |
| `Turns/IConversationRotationStore.cs` (create) | The one atomic rotation: guarded generation increment plus pending-confirmation settle. |
| `Turns/ConversationRotationService.cs` (create) | The application boundary the endpoint calls; owns `ConversationRotationView`. |
| `Inventories/IInventoryVersionStore.cs` (create) | The read seam over per-Inventory versions. |
| `Inventories/InventoryInvalidationReader.cs` (create) | One Participant's authorized Inventories with their current versions - the whole payload of the Participant-level stream. |

### Infrastructure (`src/MultiChannelAgent.Infrastructure/`)

| File | Responsibility |
| --- | --- |
| `Persistence/Entities/TurnProgressEventEntity.cs` (create) | The durable progress row: `(TurnId, Sequence)` key, kind, instant, expiry ticks. |
| `Persistence/Configurations/TurnProgressEventEntityConfiguration.cs` (create) | Composite key, bounds, expiry-sweep index, cascade from the inbox row. |
| `Persistence/Entities/InventoryVersionEntity.cs` (create) | One row per Inventory: identity and monotonic version. No clock column at all. |
| `Persistence/Configurations/InventoryVersionEntityConfiguration.cs` (create) | Key on the Inventory. Deliberately no foreign key, mirroring `InventoryAudits` (D5). |
| `Persistence/MultiChannelAgentDbContext.cs` (modify) | The publish seam: `SaveChanges`/`SaveChangesAsync` overrides that seed a version row for every new Inventory and bump the version once per Inventory that staged a non-denial audit fact, inside the caller's transaction, after the base save. |
| `Persistence/Entities/InboxEntryEntity.cs` (modify) | Nullable `FoundryConversationId` and `FoundryConversationGeneration` captured at acceptance. |
| `Persistence/Migrations/*_AddTurnProgressEvents.cs` (generate) | One table plus its expiry index. |
| `Persistence/Migrations/*_AddInventoryVersions.cs` (generate) | One table plus a backfill row for every existing Inventory. |
| `Persistence/Migrations/*_AddCapturedFoundryConversationBinding.cs` (generate) | Two nullable columns plus a join backfill from existing bindings. |
| `Turns/SqlTurnProgressEventStore.cs` (create) | Idempotent append, read, bounded delete. |
| `Turns/SqlInboxStore.cs` (modify) | Writes the captured binding at acceptance; reads it back. |
| `Turns/SqlConversationRotationStore.cs` (create) | The guarded, transactional rotation. |
| `Inventories/SqlInventoryVersionStore.cs` (create) | Bulk version read for a set of Inventories. |
| `ServiceCollectionExtensions.cs` (modify) | Registers the new stores, readers, services, and coordinator. |

### Host (`src/MultiChannelAgent.Host/`)

| File | Responsibility |
| --- | --- |
| `Endpoints/ServerSentEvents.cs` (create) | The SSE wire format in one place: response preparation, event framing, heartbeat, `Last-Event-ID` parsing, and the two injectable stream timing options. |
| `Endpoints/TurnEventStreamResult.cs` (create) | The finite per-Turn stream loop: poll in a fresh scope, write, heartbeat, stop at terminal or the interactive-wait bound, swallow disconnects. |
| `Endpoints/InventoryEventStreamResult.cs` (create) | The Participant-level snapshot-and-diff loop. |
| `Endpoints/TurnEndpoints.cs` (modify) | Maps `GET /api/turns/{turnId:guid}/events`. |
| `Endpoints/ConversationEndpoints.cs` (create) | Maps `POST /api/conversation/new`. |
| `Endpoints/InventoryEventEndpoints.cs` (create) | Maps `GET /api/inventory-events`. It is not under `/api/inventories` because it is scoped to the Participant, not to one Inventory. |
| `Workers/TurnProgressEventCleanupWorker.cs` (create) | Periodically drives the progress-log sweep. |
| `Program.cs` (modify) | Registers the worker, the stream timing options, and maps the new endpoints. |

### Web (`src/web/`)

| File | Responsibility |
| --- | --- |
| `package.json` (modify) | Vitest + Testing Library devDependencies and the `test` script. |
| `vite.config.ts` (modify) | Vitest jsdom environment and setup file. |
| `src/testing/setup.ts` (create) | jest-dom matchers, `matchMedia` stub, `localStorage` reset. |
| `src/testing/fakeEventSource.ts` (create) | A controllable `EventSource` double, and a global installer for it, since jsdom implements none. |
| `src/turnStream.ts` (create) | The typed per-Turn SSE client: event shapes, injectable factory, `Last-Event-ID` continuation, terminal close. |
| `src/inventoryStream.ts` (create) | The typed Participant-level SSE client. |
| `src/conversationStorage.ts` (create) | Browser-profile continuity: the in-flight Turn record, cross-tab `storage` subscription, and reset. |
| `src/conversationApi.ts` (create) | `startNewConversation`. |
| `.github/workflows/ci.yml` (modify) | Runs the web test suite as a gate. |
| `src/useMediaQuery.ts` (create) | `window.matchMedia` as a React hook. |
| `src/WorkspacePanel.tsx` (create) | The responsive container: `<aside>` on desktop, an accessible tab list below the breakpoint. |
| `src/turnsApi.ts` (modify) | `composeOutcome` - rebuilds a `TurnOutcomeView` from streamed parts plus the streamed terminal event, so there stays exactly one renderer. |
| `src/TurnTracer.tsx` (modify) | Streams instead of polling; resumes a stored Turn on mount; renders progress in a live region. |
| `src/App.tsx` (modify) | The conversation-primary layout, the header's always-visible Active Inventory, "New conversation", and version-driven workspace invalidation. |
| `src/index.css` (modify) | The two-column desktop grid and the single-column narrow layout. |

### Tests

| File | Responsibility |
| --- | --- |
| `tests/MultiChannelAgent.Domain.Tests/Turns/TurnStreamEventTests.cs` (create) | Fixed sequences, ordering, part bounds, issued-id recognition, retention. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryTurnProgressEventStore.cs` (create) | The progress-store double. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryInventoryVersionStore.cs` (create) | The version-store double. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryConversationRotationStore.cs` (create) | The rotation double. |
| `tests/MultiChannelAgent.Application.Tests/Turns/TurnEventReaderTests.cs` (create) | Projection, ordering, replay, non-disclosure, terminal detection, swept-log behaviour. |
| `tests/MultiChannelAgent.Application.Tests/Turns/ConversationRotationServiceTests.cs` (create) | The application-boundary view of rotation. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryInvalidationReaderTests.cs` (create) | Only authorized Inventories, missing versions default to zero, stable order. |
| `tests/MultiChannelAgent.IntegrationTests/SqlTurnProgressEventStoreTests.cs` (create) | Docker-free append idempotency, read, bounded expiry. |
| `tests/MultiChannelAgent.IntegrationTests/InventoryVersionBumpTests.cs` (create) | Docker-free proof the persistence seam bumps once per audited mutation, never on denial, and never on rollback. |
| `tests/MultiChannelAgent.IntegrationTests/TurnEventStreamHttpTests.cs` (create) | Full replay, `Last-Event-ID` resume, ignored bad ID, foreign-Turn 404, ordering, finite terminal, single-line data. |
| `tests/MultiChannelAgent.IntegrationTests/InventoryEventStreamHttpTests.cs` (create) | Snapshot on connect, cross-channel change, revocation, non-disclosure. |
| `tests/MultiChannelAgent.IntegrationTests/ConversationRotationHttpTests.cs` (create) | Rotation preserves access and selection, clears the conversational proposal, keeps the import proposal, changes the generation. |
| `tests/MultiChannelAgent.IntegrationTests/SharedBrowserProfileScenario.cs` (create) | Two clients sharing one cookie jar: one ChannelConversation, shared FIFO, resume after disconnect, no duplicate mutation. |
| `tests/MultiChannelAgent.IntegrationTests/WebConversationContinuitySqlScenarioTests.cs` (create) | Real SQL Server: migrations, concurrent rotation, rotation racing acceptance. |
| `tests/MultiChannelAgent.IntegrationTests/ServerSentEventReader.cs` (create) | A stateful, async-disposable decoder that owns one reader over a live `text/event-stream` response. One parse/dispatch state machine serves both event reads and heartbeat waits, and completed events are queued, so several sequential reads of the same response cannot lose buffered bytes *or* a whole event to a heartbeat wait. |
| `tests/MultiChannelAgent.IntegrationTests/SqlConversationRotationStoreTests.cs` (create) | Docker-free proof that rotation is atomic, guarded, and touches nothing it must not. |
| `tests/MultiChannelAgent.Application.Tests/Turns/TurnProgressEventCleanupCoordinatorTests.cs` (create) | The leased, bounded progress sweep. |
| `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs` (modify) | The progress publish; the superseded-conversation settle after dispatch; and the deterministic race in which the reset commits *while* the Turn is being processed. |
| `tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs` (modify) | The captured binding, its fallback, and superseded-generation detection. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalLifecycleTests.cs` (modify) | The superseded-conversation invalidation before dispatch, and the settlement-time binding re-read after it - including the reset that lands after the context was assembled. |
| `tests/MultiChannelAgent.Application.Tests/TurnAcceptanceServiceTests.cs` (modify) | The Foundry binding captured at acceptance. |
| `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs` (modify) | Shared-cookie second tab, SSE reader, rotation, version-stream helpers. |
| `src/web/src/turnStream.test.ts` (create) | Event decoding, resume, terminal close, error surface. |
| `src/web/src/inventoryStream.test.ts` (create) | Snapshot/changed/revoked decoding and close. |
| `src/web/src/conversationStorage.test.ts` (create) | Persist, read, clear, cross-tab notification. |
| `src/web/src/WorkspacePanel.test.tsx` (create) | Desktop `<aside>` landmark, narrow tab semantics and keyboard navigation. |
| `src/web/src/useMediaQuery.test.ts` (create) | Subscription stability across an unrelated rerender, and correct resubscription when the query changes. |
| `src/web/src/TurnTracer.test.tsx` (create) | Streamed progress and outcome, mount-time resume with no resubmission, live region. |
| `src/web/src/App.test.tsx` (create) | Conversation-primary DOM order, explicit-only selection, version-driven refetch, New conversation. |

---

## Task 1: Turn stream vocabulary and fixed event sequences

**Files:**
- Create: `src/MultiChannelAgent.Domain/Turns/TurnStreamEvent.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Turns/TurnStreamEventTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Turns/TurnStreamEventTests.cs`:

```csharp
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Turns;

/// <summary>
/// The event identities the resumable per-Turn stream is built on. They are fixed constants rather
/// than a counter so an append needs no read (and therefore cannot race), a swept progress row is
/// indistinguishable from one that was never written, and the terminal identity is knowable without
/// touching the database.
/// </summary>
public class TurnStreamEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_sequences_a_turn_issues_are_strictly_increasing_in_emission_order()
    {
        Assert.True(TurnEventSequence.Accepted < TurnEventSequence.Processing);
        Assert.True(TurnEventSequence.Processing < TurnEventSequence.ForPart(1));
        Assert.True(TurnEventSequence.ForPart(1) < TurnEventSequence.ForPart(TurnEventSequence.MaxParts));
        Assert.True(TurnEventSequence.ForPart(TurnEventSequence.MaxParts) < TurnEventSequence.Outcome);
    }

    [Fact]
    public void Response_parts_are_numbered_from_their_one_based_order()
    {
        Assert.Equal(100L, TurnEventSequence.ForPart(1));
        Assert.Equal(101L, TurnEventSequence.ForPart(2));
        Assert.Equal(163L, TurnEventSequence.ForPart(64));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void A_part_order_outside_the_bound_is_refused(int order) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnEventSequence.ForPart(order));

    [Fact]
    public void Only_sequences_this_application_issues_are_recognized()
    {
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.Accepted));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.Processing));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.ForPart(1)));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.ForPart(TurnEventSequence.MaxParts)));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.Outcome));

        Assert.False(TurnEventSequence.IsIssued(0L));
        Assert.False(TurnEventSequence.IsIssued(3L));
        Assert.False(TurnEventSequence.IsIssued(TurnEventSequence.ForPart(TurnEventSequence.MaxParts) + 1));
        Assert.False(TurnEventSequence.IsIssued(TurnEventSequence.Outcome + 1));
        Assert.False(TurnEventSequence.IsIssued(-1L));
    }

    [Fact]
    public void Every_event_kind_has_stable_machine_text()
    {
        Assert.Equal("accepted", TurnEventKind.Accepted.ToMachineText());
        Assert.Equal("processing", TurnEventKind.Processing.ToMachineText());
        Assert.Equal("part", TurnEventKind.Part.ToMachineText());
        Assert.Equal("outcome", TurnEventKind.Outcome.ToMachineText());
        Assert.Equal("text", TurnResponsePartKind.Text.ToMachineText());
        Assert.Equal("data", TurnResponsePartKind.Data.ToMachineText());
    }

    [Fact]
    public void A_progress_event_carries_the_processing_sequence_and_the_shared_retention_window()
    {
        var turnId = TurnId.NewId();

        var progress = TurnProgressEvent.Processing(turnId, Now);

        Assert.Equal(turnId, progress.TurnId);
        Assert.Equal(TurnEventSequence.Processing, progress.Sequence);
        Assert.Equal(TurnEventKind.Processing, progress.Kind);
        Assert.Equal(Now, progress.OccurredAt);
        Assert.Equal(Now + TurnProgressEvent.Retention, progress.ExpiresAt);
    }

    [Fact]
    public void Progress_is_retained_exactly_as_long_as_a_recorded_outcome_payload()
    {
        // One number, so an operator never has to reason about two retention windows for the same
        // reconnect: the progress marker and the answer it precedes expire together.
        Assert.Equal(Outcome.PayloadRetention, TurnProgressEvent.Retention);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests --filter FullyQualifiedName~TurnStreamEventTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'TurnEventSequence' could not be found` (and the same for `TurnEventKind`, `TurnResponsePartKind`, `TurnProgressEvent`).

- [ ] **Step 3: Write the implementation**

Create `src/MultiChannelAgent.Domain/Turns/TurnStreamEvent.cs`:

```csharp
namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// What one event on a Turn's resumable stream reports. Deliberately a closed set of application-owned
/// semantic events - never raw model tokens, which this system does not expose on any channel.
/// </summary>
public enum TurnEventKind
{
    /// <summary>The Turn was durably accepted and now has a stable identity.</summary>
    Accepted,

    /// <summary>The Turn was claimed and is being worked on. The only progress signal durable state cannot express.</summary>
    Processing,

    /// <summary>One channel-neutral piece of the answer's content.</summary>
    Part,

    /// <summary>The one terminal Outcome. Nothing follows it.</summary>
    Outcome,
}

/// <summary>What one response part carries: renderable text, or an application-owned typed projection.</summary>
public enum TurnResponsePartKind
{
    Text,
    Data,
}

/// <summary>The stable machine text each stream vocabulary value is exposed as at the application boundary.</summary>
public static class TurnEventKindExtensions
{
    public static string ToMachineText(this TurnEventKind kind) => kind switch
    {
        TurnEventKind.Accepted => "accepted",
        TurnEventKind.Processing => "processing",
        TurnEventKind.Part => "part",
        TurnEventKind.Outcome => "outcome",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled turn event kind."),
    };

    public static string ToMachineText(this TurnResponsePartKind kind) => kind switch
    {
        TurnResponsePartKind.Text => "text",
        TurnResponsePartKind.Data => "data",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled turn response part kind."),
    };
}

/// <summary>
/// The event identity of every event a Turn can ever emit, as fixed constants rather than a running
/// counter. Three properties follow from that, and all three matter:
///
/// 1. An append is idempotent by its own identity, so writing a progress event needs no counter read
///    and therefore has none of the read-then-write contention an assigned sequence would create.
/// 2. A progress row that retention has swept is indistinguishable from one that was never written,
///    so replay needs no special case for an aged stream.
/// 3. The terminal identity is a compile-time constant, so "you already have everything" is
///    answerable without reading anything at all.
///
/// Gaps between the constants are deliberate and harmless: Server-Sent Events only requires that the
/// server understands the identifiers it issued, never that they are contiguous.
/// </summary>
public static class TurnEventSequence
{
    public const long Accepted = 1L;

    public const long Processing = 2L;

    /// <summary>The identity of the first response part. Parts are numbered from here in their own order.</summary>
    public const long FirstPart = 100L;

    /// <summary>
    /// The most response parts one Turn's answer may carry. Bounded so the part range can never
    /// collide with <see cref="Outcome"/>, and so a malformed answer cannot produce an unbounded
    /// stream.
    /// </summary>
    public const int MaxParts = 64;

    /// <summary>The terminal identity. Nothing a Turn emits is ever greater than this.</summary>
    public const long Outcome = 1_000_000L;

    private const long LastPart = FirstPart + MaxParts - 1;

    public static long ForPart(int order)
    {
        if (order < 1 || order > MaxParts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(order), order, $"A response part order must be between 1 and {MaxParts}.");
        }

        return FirstPart + order - 1;
    }

    /// <summary>
    /// Whether <paramref name="sequence"/> is an identity this application actually issues. A resumed
    /// stream validates the value it was handed against this before trusting it as a position, so a
    /// tampered, corrupted, or simply stale identifier is treated exactly as if none had been sent
    /// rather than silently skipping events the caller never received.
    /// </summary>
    public static bool IsIssued(long sequence) =>
        sequence is Accepted or Processing or Outcome || (sequence >= FirstPart && sequence <= LastPart);
}

/// <summary>
/// The one durable event on a Turn's stream. Everything else a stream reports - that the Turn was
/// accepted, what its answer's parts are, and its terminal Outcome - is projected from state this
/// system already keeps permanently, so it needs no second copy and can never disagree with the
/// authoritative record. Progress is the exception: nothing durable distinguishes "accepted and
/// waiting" from "accepted and being worked on", so that one fact is recorded here.
/// </summary>
public sealed record TurnProgressEvent
{
    /// <summary>
    /// How long a progress marker is kept. Identical to <see cref="Outcome.PayloadRetention"/> on
    /// purpose: the marker exists only so a Participant reconnecting to an in-flight answer can see
    /// that work started, and it stops being interesting at exactly the moment the answer's own
    /// retained projection does.
    /// </summary>
    public static readonly TimeSpan Retention = Outcome.PayloadRetention;

    public required TurnId TurnId { get; init; }

    public required long Sequence { get; init; }

    public required TurnEventKind Kind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public static TurnProgressEvent Processing(TurnId turnId, DateTimeOffset occurredAt) => new()
    {
        TurnId = turnId,
        Sequence = TurnEventSequence.Processing,
        Kind = TurnEventKind.Processing,
        OccurredAt = occurredAt,
        ExpiresAt = occurredAt + Retention,
    };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests --filter FullyQualifiedName~TurnStreamEventTests`
Expected: PASS, 7 tests (the `[Theory]` contributes 3 cases, so 9 test results).

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Turns/TurnStreamEvent.cs tests/MultiChannelAgent.Domain.Tests/Turns/TurnStreamEventTests.cs
git commit -m "feat: add turn stream event vocabulary and fixed event sequences"
```

---

## Task 2: The durable progress seam and its publisher

**Files:**
- Create: `src/MultiChannelAgent.Application/Turns/ITurnProgressEventStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryTurnProgressEventStore.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs`

- [ ] **Step 1: Write the failing test**

`TurnProcessingCoordinatorTests` builds every coordinator it tests in one private helper, `CreateCoordinator(TimeProvider, IModelBoundary?)`, and nine call sites destructure its six-element tuple. Extend that helper with an **optional** parameter rather than changing the tuple, so none of those nine destructurings has to move.

In `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs`, change the helper's signature to:

```csharp
    private static (TurnProcessingCoordinator Coordinator, InMemoryInboxStore Inbox, InMemoryOutcomeStore Outcomes, InMemoryDeliveryStore Deliveries, InMemoryTurnResultStore ResultStore, InMemoryFoundryConversationBindingStore Bindings)
        CreateCoordinator(
            TimeProvider timeProvider,
            IModelBoundary? modelBoundary = null,
            InMemoryTurnProgressEventStore? progressEvents = null)
```

and inside it, immediately before the `new TurnProcessingCoordinator(` call, add:

```csharp
        var progressEventStore = progressEvents ?? new InMemoryTurnProgressEventStore();
```

then add `progressEventStore,` to that constructor call immediately after `resultStore,`.

Now append these two tests to the same file, inside the existing class:

```csharp
    [Fact]
    public async Task Processing_a_turn_publishes_a_progress_event_before_the_model_is_asked_anything()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var progressEvents = new InMemoryTurnProgressEventStore();
        var scriptedModel = new ScriptedModelBoundary();
        var (coordinator, inbox, _, _, _, _) = CreateCoordinator(
            timeProvider,
            new ProgressObservingModelBoundary(scriptedModel, progressEvents),
            progressEvents);

        var turn = TestTurns.Text("native-progress-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        var published = Assert.Single(await progressEvents.ReadAsync(turn.TurnId, CancellationToken.None));
        Assert.Equal(TurnEventKind.Processing, published.Kind);
        Assert.Equal(TurnEventSequence.Processing, published.Sequence);
        Assert.True(
            progressEvents.WasAppendedBeforeFirstModelCall,
            "Progress must be published before the model boundary is asked anything, so a Participant "
            + "watching a stream sees that work started rather than silence.");
    }

    [Fact]
    public async Task Reprocessing_a_turn_after_a_failed_attempt_never_publishes_a_second_progress_event()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var progressEvents = new InMemoryTurnProgressEventStore();
        var (coordinator, inbox, _, _, resultStore, _) = CreateCoordinator(
            timeProvider, modelBoundary: null, progressEvents: progressEvents);

        var turn = TestTurns.Text("native-progress-2", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        resultStore.FailNextRecord = true;
        await coordinator.ProcessPendingAsync(CancellationToken.None);
        await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Single(await progressEvents.ReadAsync(turn.TurnId, CancellationToken.None));
    }
```

Add the observing boundary as a nested private class in the same file, next to the existing `CountingModelBoundary` and `CapturingModelBoundary`:

```csharp
    /// <summary>Records the moment the model is first asked anything, so a test can prove progress was published before it.</summary>
    private sealed class ProgressObservingModelBoundary(IModelBoundary inner, InMemoryTurnProgressEventStore progressEvents)
        : IModelBoundary
    {
        public Task<ModelProposal> ProposeAsync(InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
        {
            progressEvents.ModelWasCalled = true;
            return inner.ProposeAsync(turn, context, cancellationToken);
        }
    }
```

`InMemoryTurnResultStore` has no `FailNextRecord` yet. Add it to `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryTurnResultStore.cs`:

```csharp
    /// <summary>Provokes exactly one failed atomic record, so a test can prove a retry is safe.</summary>
    public bool FailNextRecord { get; set; }
```

and at the top of its `RecordAsync`:

```csharp
        if (FailNextRecord)
        {
            FailNextRecord = false;
            throw new InvalidOperationException("Provoked terminal record failure.");
        }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnProcessingCoordinatorTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'InMemoryTurnProgressEventStore' could not be found`.

- [ ] **Step 3: Write the seam**

Create `src/MultiChannelAgent.Application/Turns/ITurnProgressEventStore.cs`:

```csharp
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// The durable home of the one Turn stream event that state cannot be projected from: that a claimed
/// Turn is being worked on.
///
/// Appending is idempotent by the event's own identity - <see cref="TurnProgressEvent.TurnId"/> with
/// <see cref="TurnProgressEvent.Sequence"/> - never by a counter this store assigns, so a Turn whose
/// first processing attempt failed can be retried without ever producing a second marker and without
/// two replicas needing to agree on a next number. Implementations must express that atomically
/// rather than by looking first: <see cref="AppendAsync"/> reports whether this call was the one that
/// wrote it, and a concurrent duplicate is a normal "false", never a store-specific exception.
/// </summary>
public interface ITurnProgressEventStore
{
    /// <summary>
    /// Records <paramref name="progressEvent"/> unless its identity is already recorded. Returns true
    /// when this call wrote it.
    /// </summary>
    Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken);

    /// <summary>Every retained progress event for one Turn, in ascending sequence order.</summary>
    Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="maxCount"/> progress events whose retention has passed, and
    /// reports how many were deleted. Never more than <paramref name="maxCount"/> rows, and never a
    /// row it did not select: an implementation must delete the actual
    /// (<see cref="TurnProgressEvent.TurnId"/>, <see cref="TurnProgressEvent.Sequence"/>) identities
    /// it chose, not everything matching the two sets those identities happen to span. The Turn's
    /// Outcome is untouched: only the marker that preceded it is dropped, so a Turn never stops
    /// having an answer.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Write the test double**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryTurnProgressEventStore.cs`:

```csharp
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="ITurnProgressEventStore"/> for Application-layer unit tests. A single
/// lock makes "is this identity already recorded, and if not record it" one indivisible step, exactly
/// as the real store's composite primary key does, so a concurrent duplicate converges instead of
/// producing a second marker.
/// </summary>
public sealed class InMemoryTurnProgressEventStore : ITurnProgressEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid TurnId, long Sequence), TurnProgressEvent> _events = [];

    /// <summary>
    /// Set by the harness when the scripted model boundary is first asked for a proposal, so a test
    /// can prove progress is published before that happens rather than after.
    /// </summary>
    public bool ModelWasCalled { get; set; }

    public bool WasAppendedBeforeFirstModelCall { get; private set; }

    public Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        lock (_gate)
        {
            var key = (progressEvent.TurnId.Value, progressEvent.Sequence);
            if (!_events.TryAdd(key, progressEvent))
            {
                return Task.FromResult(false);
            }

            if (!ModelWasCalled)
            {
                WasAppendedBeforeFirstModelCall = true;
            }

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<TurnProgressEvent> events = _events.Values
                .Where(e => e.TurnId == turnId)
                .OrderBy(e => e.Sequence)
                .ToList();

            return Task.FromResult(events);
        }
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var expired = _events
                .Where(pair => pair.Value.ExpiresAt <= now)
                .OrderBy(pair => pair.Value.ExpiresAt)
                .Take(maxCount)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in expired)
            {
                _events.Remove(key);
            }

            return Task.FromResult(expired.Count);
        }
    }
}
```

In the test file's `ProgressObservingModelBoundary` (added in Step 1), `ModelWasCalled` is set on the first `ProposeAsync`, which is what makes `WasAppendedBeforeFirstModelCall` meaningful.

- [ ] **Step 5: Publish progress from the coordinator**

In `src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs`, add `ITurnProgressEventStore progressEventStore,` to the primary constructor parameter list immediately after `ITurnResultStore turnResultStore,`.

Every construction site of that coordinator must be updated in the same commit. There are exactly two, and both are tests:

| Site | Change |
| --- | --- |
| `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs`, in `CreateCoordinator` | Done in Step 1: pass `progressEventStore,` after `resultStore,`. |
| `tests/MultiChannelAgent.IntegrationTests/PerConversationFifoScenario.cs`, in `RunPassAsync` | Add `services.GetRequiredService<ITurnProgressEventStore>(),` immediately after `services.GetRequiredService<ITurnResultStore>(),`. The registration is added in Task 4; until then this scenario resolves nothing new, so run this task's tests first and the full suite after Task 4. |

Production resolves the coordinator from DI, so no production call site changes.

Then, in `ProcessOneAsync`, insert the publish immediately after the `proposalLifecycle.ReconcileAsync(...)` call and before `modelBoundary.ProposeAsync(...)`:

```csharp
        // Published before the model is asked anything, and idempotently by the event's own identity,
        // so a Participant watching this Turn's stream sees that work started rather than silence -
        // and a retry after a failed attempt re-publishes nothing. It is deliberately a separate,
        // non-atomic write: a progress marker is a courtesy, and losing one must never stop a Turn
        // from reaching the terminal Outcome that ITurnResultStore records atomically.
        await progressEventStore.AppendAsync(TurnProgressEvent.Processing(turn.TurnId, now), cancellationToken);
```

Extend the class summary's second paragraph with:

```
/// Before it asks the model anything it publishes one durable progress event for the Turn, which is
/// what makes a reconnecting web client able to tell "accepted and waiting" from "being worked on".
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnProcessingCoordinatorTests`
Expected: PASS, including the two new tests.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/ITurnProgressEventStore.cs \
        src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs \
        tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryTurnProgressEventStore.cs \
        tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryTurnResultStore.cs \
        tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs \
        tests/MultiChannelAgent.IntegrationTests/PerConversationFifoScenario.cs
git commit -m "feat: publish a durable processing progress event for every claimed Turn"
```

---

## Task 3: TurnEventReader - the one authority on a Turn's event stream

**Files:**
- Create: `src/MultiChannelAgent.Application/Turns/TurnEventReader.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Turns/TurnEventReaderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Turns/TurnEventReaderTests.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Turns;

/// <summary>
/// The single seam that decides what one Turn's resumable event stream is. Everything the HTTP
/// endpoint does is serialize what this returns, so every rule that matters - non-disclosure,
/// ordering, replay after a resume point, and where the stream ends - is proved here without HTTP.
/// </summary>
public class TurnEventReaderTests
{
    private static readonly DateTimeOffset Accepted = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = Accepted.AddSeconds(1);
    private static readonly DateTimeOffset Answered = Accepted.AddSeconds(2);

    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly InMemoryInboxStore _inbox = new();
    private readonly InMemoryTurnProgressEventStore _progress = new();
    private readonly InMemoryOutcomeStore _outcomes = new();
    private readonly InMemoryDeliveryStore _deliveries = new();

    private TurnEventReader Reader => new(_inbox, _progress, _outcomes, _deliveries);

    [Fact]
    public async Task An_unknown_turn_and_another_participants_turn_are_indistinguishable()
    {
        var turnId = await AcceptAsync();

        Assert.Null(await Reader.ReadAfterAsync(turnId, Stranger, 0L, CancellationToken.None));
        Assert.Null(await Reader.ReadAfterAsync(TurnId.NewId(), Owner, 0L, CancellationToken.None));
    }

    [Fact]
    public async Task A_just_accepted_turn_streams_only_its_acceptance_and_is_not_terminal()
    {
        var turnId = await AcceptAsync();

        var page = await Reader.ReadAfterAsync(turnId, Owner, 0L, CancellationToken.None);

        Assert.NotNull(page);
        Assert.False(page.ReachedTerminal);
        var only = Assert.Single(page.Events);
        Assert.Equal(TurnEventSequence.Accepted, only.Sequence);
        Assert.Equal("accepted", only.Name);
        Assert.Equal(Accepted, JsonDocument.Parse(only.Data).RootElement.GetProperty("receivedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_turn_being_worked_on_streams_acceptance_then_progress_and_is_still_not_terminal()
    {
        var turnId = await AcceptAsync();
        await _progress.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, 0L, CancellationToken.None);

        Assert.NotNull(page);
        Assert.False(page.ReachedTerminal);
        Assert.Equal(
            new[] { TurnEventSequence.Accepted, TurnEventSequence.Processing },
            page.Events.Select(e => e.Sequence));
        Assert.Equal(["accepted", "processing"], page.Events.Select(e => e.Name));
    }

    [Fact]
    public async Task An_answered_turn_streams_its_semantic_parts_then_one_terminal_outcome()
    {
        var turnId = await AcceptAsync();
        await _progress.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None);
        await _outcomes.SaveAsync(
            Outcome.Completed(turnId, "stock.listed", "2 Stock Entries.", Answered, """{"version":1,"kind":"stock_list"}"""),
            CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, 0L, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.ReachedTerminal);
        Assert.Equal(
            new[]
            {
                TurnEventSequence.Accepted,
                TurnEventSequence.Processing,
                TurnEventSequence.ForPart(1),
                TurnEventSequence.ForPart(2),
                TurnEventSequence.Outcome,
            },
            page.Events.Select(e => e.Sequence));

        var textPart = JsonDocument.Parse(page.Events[2].Data).RootElement;
        Assert.Equal("text", textPart.GetProperty("kind").GetString());
        Assert.Equal("2 Stock Entries.", textPart.GetProperty("text").GetString());

        var dataPart = JsonDocument.Parse(page.Events[3].Data).RootElement;
        Assert.Equal("data", dataPart.GetProperty("kind").GetString());
        Assert.Equal("stock_list", dataPart.GetProperty("payload").GetProperty("kind").GetString());

        var terminal = JsonDocument.Parse(page.Events[4].Data).RootElement;
        Assert.Equal("outcome", page.Events[4].Name);
        Assert.Equal("completed", terminal.GetProperty("status").GetString());
        Assert.Equal("completed", terminal.GetProperty("category").GetString());
        Assert.Equal("stock.listed", terminal.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_answer_with_no_retained_payload_streams_exactly_one_text_part()
    {
        var turnId = await AcceptAsync();
        await _outcomes.SaveAsync(Outcome.Completed(turnId, "echo", "You said hello.", Answered), CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, 0L, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(
            new[] { TurnEventSequence.Accepted, TurnEventSequence.ForPart(1), TurnEventSequence.Outcome },
            page.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Resuming_after_an_event_replays_only_what_comes_after_it()
    {
        var turnId = await AcceptAsync();
        await _progress.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None);
        await _outcomes.SaveAsync(Outcome.Completed(turnId, "echo", "You said hello.", Answered), CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, TurnEventSequence.Processing, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.ReachedTerminal);
        Assert.Equal(new[] { TurnEventSequence.ForPart(1), TurnEventSequence.Outcome }, page.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Resuming_from_the_terminal_event_reports_the_stream_finished_with_nothing_left()
    {
        var turnId = await AcceptAsync();
        await _outcomes.SaveAsync(Outcome.Completed(turnId, "echo", "You said hello.", Answered), CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, TurnEventSequence.Outcome, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.ReachedTerminal);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task A_swept_progress_marker_never_stops_an_answered_turn_from_replaying_its_answer()
    {
        var turnId = await AcceptAsync();
        await _progress.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None);
        await _outcomes.SaveAsync(Outcome.Completed(turnId, "echo", "You said hello.", Answered), CancellationToken.None);
        await _progress.DeleteExpiredAsync(Answered + TurnProgressEvent.Retention, 100, CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, 0L, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.ReachedTerminal);
        Assert.Equal(
            new[] { TurnEventSequence.Accepted, TurnEventSequence.ForPart(1), TurnEventSequence.Outcome },
            page.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Every_events_data_is_a_single_line_so_it_can_never_break_the_wire_framing()
    {
        var turnId = await AcceptAsync();
        await _outcomes.SaveAsync(
            Outcome.Completed(turnId, "echo", "First line.\nSecond line.", Answered, """{"version":1,"kind":"stock_list"}"""),
            CancellationToken.None);

        var page = await Reader.ReadAfterAsync(turnId, Owner, 0L, CancellationToken.None);

        Assert.NotNull(page);
        Assert.All(page.Events, e => Assert.DoesNotContain('\n', e.Data));
        Assert.All(page.Events, e => Assert.DoesNotContain('\r', e.Data));
    }

    private async Task<TurnId> AcceptAsync()
    {
        var turn = InboundTurn.Create(InboundTurnDraft.DirectText(
            "native-1",
            Owner,
            "conversation-1",
            "web",
            ChannelPrincipal.EntraUser(Owner.Value.ToString(), "tenant-1"),
            ChannelCapabilities.Text | ChannelCapabilities.RichText | ChannelCapabilities.ProgressEvents,
            "hello",
            locale: null,
            Accepted,
            traceId: null,
            wasInterrupted: false));

        var accepted = await _inbox.AcceptAsync(turn, CancellationToken.None);
        return accepted.Turn.TurnId;
    }
}
```

> If Task 9 has already landed, `_inbox.AcceptAsync(turn, CancellationToken.None)` still compiles:
> `InMemoryInboxStore` keeps a one-argument convenience overload that accepts into a first-generation
> binding, which is exactly what this test wants. Tasks are ordered so this one lands first anyway.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnEventReaderTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'TurnEventReader' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/MultiChannelAgent.Application/Turns/TurnEventReader.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>One event on a Turn's stream: its issued identity, its name, and its already-serialized single-line JSON body.</summary>
public sealed record TurnStreamEvent(long Sequence, string Name, string Data);

/// <summary>
/// Everything a Turn's stream has to say beyond a given resume point, and whether the stream is
/// finished. <see cref="ReachedTerminal"/> is true exactly when the Turn has a recorded Outcome -
/// including when the caller already had it - so a channel adapter knows to stop rather than to
/// guess from an empty page.
/// </summary>
public sealed record TurnEventPage(IReadOnlyList<TurnStreamEvent> Events, bool ReachedTerminal);

/// <summary>Wire body of the <c>accepted</c> event.</summary>
public sealed record TurnAcceptedData(Guid TurnId, DateTimeOffset ReceivedAt);

/// <summary>Wire body of the <c>processing</c> event.</summary>
public sealed record TurnProcessingData(Guid TurnId, DateTimeOffset StartedAt);

/// <summary>
/// Wire body of one <c>part</c> event: one channel-neutral piece of the answer. Exactly one of
/// <see cref="Text"/> and <see cref="Payload"/> is ever present, and neither is ever a raw model
/// token - a text part is the recorded human summary and a data part is the recorded typed
/// projection.
/// </summary>
public sealed record TurnResponsePartData(Guid TurnId, int Order, string Kind, string? Text, JsonElement? Payload);

/// <summary>
/// Wire body of the one terminal <c>outcome</c> event. It deliberately carries no payload: the typed
/// projection already arrived as a data part, so the stream never sends it twice - which also means a
/// short-lived confirmation token is never duplicated on the wire.
/// </summary>
public sealed record TurnStreamOutcomeData(
    Guid TurnId, string Status, string Category, string Code, string Summary, IReadOnlyList<DeliveryView> Deliveries);

/// <summary>
/// The single authority on what one Turn's resumable event stream is. Channel adapters serialize what
/// this returns and nothing more, so every rule the stream depends on lives here exactly once.
///
/// Only one of the four event kinds is read from a durable event row. The others are projected from
/// state this system already keeps permanently - acceptance from the Turn's own inbox record, the
/// answer's parts and its terminal Outcome from the recorded <see cref="Outcome"/> and its
/// Deliveries - which is what makes the stream survive a process restart, replay identically however
/// often it is resumed, and hold no second copy of a payload whose retention is already governed
/// elsewhere. When that payload has expired, the answer simply streams without its data part, exactly
/// as <see cref="TurnOutcomeReader"/> already serves it without one.
///
/// A caller may only ever read their own Turn: <see cref="ReadAfterAsync"/> returns null - the same
/// shape as "no such Turn" - for a Turn that exists but belongs to a different Participant, so a
/// caller can never learn that some other Participant's Turn exists.
/// </summary>
public sealed class TurnEventReader(
    IInboxStore inboxStore,
    ITurnProgressEventStore progressEventStore,
    IOutcomeStore outcomeStore,
    IDeliveryStore deliveryStore)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<TurnEventPage?> ReadAfterAsync(
        TurnId turnId, ParticipantId requestingParticipantId, long afterSequence, CancellationToken cancellationToken)
    {
        var turn = await inboxStore.FindByTurnIdAsync(turnId, cancellationToken);
        if (turn is null || turn.ParticipantId != requestingParticipantId)
        {
            return null;
        }

        var events = new List<TurnStreamEvent>();

        if (afterSequence < TurnEventSequence.Accepted)
        {
            events.Add(Event(
                TurnEventSequence.Accepted,
                TurnEventKind.Accepted,
                new TurnAcceptedData(turnId.Value, turn.ReceivedAt)));
        }

        if (afterSequence < TurnEventSequence.Processing)
        {
            var progress = await progressEventStore.ReadAsync(turnId, cancellationToken);
            if (progress.FirstOrDefault(e => e.Kind == TurnEventKind.Processing) is { } processing)
            {
                events.Add(Event(
                    TurnEventSequence.Processing,
                    TurnEventKind.Processing,
                    new TurnProcessingData(turnId.Value, processing.OccurredAt)));
            }
        }

        var outcome = await outcomeStore.FindAsync(turnId, cancellationToken);
        if (outcome is null)
        {
            return new TurnEventPage(events, ReachedTerminal: false);
        }

        foreach (var (sequence, part) in ResponseParts(turnId, outcome))
        {
            if (afterSequence < sequence)
            {
                events.Add(Event(sequence, TurnEventKind.Part, part));
            }
        }

        if (afterSequence < TurnEventSequence.Outcome)
        {
            var deliveries = await deliveryStore.FindByTurnIdAsync(turnId, cancellationToken);

            events.Add(Event(
                TurnEventSequence.Outcome,
                TurnEventKind.Outcome,
                new TurnStreamOutcomeData(
                    turnId.Value,
                    outcome.Status.ToString().ToLowerInvariant(),
                    outcome.Category.ToMachineText(),
                    outcome.Code,
                    outcome.Summary,
                    deliveries
                        .Select(d => new DeliveryView(d.DeliveryId, d.Channel, d.Status.ToString().ToLowerInvariant(), d.Attempts))
                        .ToList())));
        }

        return new TurnEventPage(events, ReachedTerminal: true);
    }

    /// <summary>
    /// The channel-neutral pieces of one recorded answer, in the order a channel renders them: the
    /// human summary first, then the typed projection when the Outcome still retains one. This is the
    /// whole definition of "semantic response parts" in this system - there is no second source, and
    /// there are never raw model tokens.
    /// </summary>
    private static IReadOnlyList<(long Sequence, TurnResponsePartData Part)> ResponseParts(TurnId turnId, Outcome outcome)
    {
        var parts = new List<(long, TurnResponsePartData)>
        {
            (TurnEventSequence.ForPart(1), new TurnResponsePartData(
                turnId.Value, 1, TurnResponsePartKind.Text.ToMachineText(), outcome.Summary, null)),
        };

        if (outcome.Payload is { } payload)
        {
            parts.Add((TurnEventSequence.ForPart(2), new TurnResponsePartData(
                turnId.Value,
                2,
                TurnResponsePartKind.Data.ToMachineText(),
                null,
                JsonSerializer.Deserialize<JsonElement>(payload))));
        }

        return parts;
    }

    private static TurnStreamEvent Event<TData>(long sequence, TurnEventKind kind, TData data) =>
        new(sequence, kind.ToMachineText(), JsonSerializer.Serialize(data, SerializerOptions));
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnEventReaderTests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/TurnEventReader.cs tests/MultiChannelAgent.Application.Tests/Turns/TurnEventReaderTests.cs
git commit -m "feat: add TurnEventReader as the one authority on a Turn's resumable event stream"
```

---

## Task 4: Persist progress events

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/TurnProgressEventEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/TurnProgressEventEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Turns/SqlTurnProgressEventStore.cs`
- Generate: `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/*_AddTurnProgressEvents.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlTurnProgressEventStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/SqlTurnProgressEventStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage (a real relational engine, not mocks) of the one durable event a Turn's
/// stream carries. The properties that matter are all database properties: appending is idempotent by
/// the event's own identity rather than by looking first, a second replica racing the same append
/// converges instead of duplicating, and retention deletes only the marker while the Turn's Outcome
/// stays exactly where it was.
/// </summary>
public sealed class SqlTurnProgressEventStoreTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlTurnProgressEventStoreTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task An_appended_progress_event_reads_back_with_its_issued_identity()
    {
        using var db = CreateContext();
        var turnId = await SeedTurnAsync(db, "native-1");
        var store = new SqlTurnProgressEventStore(db);

        Assert.True(await store.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None));

        var read = Assert.Single(await store.ReadAsync(turnId, CancellationToken.None));
        Assert.Equal(turnId, read.TurnId);
        Assert.Equal(TurnEventSequence.Processing, read.Sequence);
        Assert.Equal(TurnEventKind.Processing, read.Kind);
        Assert.Equal(Started, read.OccurredAt);
        Assert.Equal(Started + TurnProgressEvent.Retention, read.ExpiresAt);
    }

    [Fact]
    public async Task Appending_the_same_identity_twice_records_it_once_and_reports_the_second_call_as_a_no_op()
    {
        using var db = CreateContext();
        var turnId = await SeedTurnAsync(db, "native-2");
        var store = new SqlTurnProgressEventStore(db);

        Assert.True(await store.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None));
        Assert.False(await store.AppendAsync(TurnProgressEvent.Processing(turnId, Started.AddMinutes(1)), CancellationToken.None));

        var read = Assert.Single(await store.ReadAsync(turnId, CancellationToken.None));
        Assert.Equal(Started, read.OccurredAt);
    }

    [Fact]
    public async Task A_second_writer_reaching_the_same_identity_converges_on_the_one_row()
    {
        using var seedDb = CreateContext();
        var turnId = await SeedTurnAsync(seedDb, "native-3");

        using var firstDb = CreateContext();
        using var secondDb = CreateContext();

        // Two independent contexts reaching the same identity. Whether the engine actually interleaves
        // them is up to it - SQLite serializes writers, SQL Server may not - and the assertion is
        // written to be true either way: exactly one call reports having written the row, and exactly
        // one row exists. The name says "a second writer" rather than "concurrent writers" because
        // that is what this test can honestly guarantee it exercised.
        var results = await Task.WhenAll(
            new SqlTurnProgressEventStore(firstDb).AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None),
            new SqlTurnProgressEventStore(secondDb).AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None));

        Assert.Single(results, wrote => wrote);

        using var verifyDb = CreateContext();
        Assert.Equal(1, await verifyDb.TurnProgressEvents.AsNoTracking().CountAsync(e => e.TurnId == turnId.Value));
    }

    [Fact]
    public async Task Retention_deletes_only_expired_markers_and_never_the_turn_behind_them()
    {
        using var db = CreateContext();
        var expiredTurn = await SeedTurnAsync(db, "native-4");
        var freshTurn = await SeedTurnAsync(db, "native-5");
        var store = new SqlTurnProgressEventStore(db);

        await store.AppendAsync(TurnProgressEvent.Processing(expiredTurn, Started), CancellationToken.None);
        await store.AppendAsync(TurnProgressEvent.Processing(freshTurn, Started.AddHours(23)), CancellationToken.None);

        var deleted = await store.DeleteExpiredAsync(Started + TurnProgressEvent.Retention, 100, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Empty(await store.ReadAsync(expiredTurn, CancellationToken.None));
        Assert.Single(await store.ReadAsync(freshTurn, CancellationToken.None));

        using var verifyDb = CreateContext();
        Assert.Equal(2, await verifyDb.InboxEntries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task One_retention_pass_never_deletes_more_than_it_was_asked_to()
    {
        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);

        for (var i = 0; i < 5; i++)
        {
            var turnId = await SeedTurnAsync(db, $"native-batch-{i}");
            await store.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None);
        }

        Assert.Equal(2, await store.DeleteExpiredAsync(Started + TurnProgressEvent.Retention, 2, CancellationToken.None));
        Assert.Equal(3, await store.DeleteExpiredAsync(Started + TurnProgressEvent.Retention, 100, CancellationToken.None));
    }

    [Fact]
    public async Task A_bounded_pass_deletes_the_identities_it_selected_and_never_their_cross_product()
    {
        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);

        var first = await SeedTurnAsync(db, "native-pairs-1");
        var second = await SeedTurnAsync(db, "native-pairs-2");

        // Four expired markers across two Turns and two identities, arranged so the two OLDEST are
        // (first, Processing) and (second, Processing + 1) - one identity from each Turn, and a
        // different identity from each. A pass bounded to two must delete exactly those two rows. An
        // implementation that deleted "every row whose Turn is in the selected set AND whose sequence
        // is in the selected set" would delete all four, which is why this arrangement and not a
        // simpler one.
        await store.AppendAsync(Marker(first, TurnEventSequence.Processing, Started), CancellationToken.None);
        await store.AppendAsync(Marker(second, TurnEventSequence.Processing + 1, Started), CancellationToken.None);
        await store.AppendAsync(Marker(first, TurnEventSequence.Processing + 1, Started.AddMinutes(1)), CancellationToken.None);
        await store.AppendAsync(Marker(second, TurnEventSequence.Processing, Started.AddMinutes(1)), CancellationToken.None);

        var deleted = await store.DeleteExpiredAsync(
            Started.AddMinutes(2) + TurnProgressEvent.Retention, 2, CancellationToken.None);

        Assert.Equal(2, deleted);

        using var verifyDb = CreateContext();
        var remaining = await verifyDb.TurnProgressEvents.AsNoTracking()
            .OrderBy(e => e.ExpiresAtTicks)
            .Select(e => new { e.TurnId, e.Sequence })
            .ToListAsync();

        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, r => r.TurnId == first.Value && r.Sequence == TurnEventSequence.Processing + 1);
        Assert.Contains(remaining, r => r.TurnId == second.Value && r.Sequence == TurnEventSequence.Processing);
    }

    /// <summary>
    /// One durable marker at an explicit identity and instant. The vocabulary issues exactly one
    /// progress identity today, so the second identity used above is deliberately one it does not:
    /// which identities are meaningful is a decision <see cref="TurnEventSequence.IsIssued"/> makes
    /// for the reader and the endpoint, while the store's whole job is to be correct about the
    /// composite identity it is keyed on. A bounded delete that quietly assumed one row per Turn
    /// would be a defect waiting for the first day that assumption stops holding.
    /// </summary>
    private static TurnProgressEvent Marker(TurnId turnId, long sequence, DateTimeOffset occurredAt) => new()
    {
        TurnId = turnId,
        Sequence = sequence,
        Kind = TurnEventKind.Processing,
        OccurredAt = occurredAt,
        ExpiresAt = occurredAt + TurnProgressEvent.Retention,
    };

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    /// <summary>A progress event may only ever exist for a durably accepted Turn, so every test needs one.</summary>
    private static async Task<TurnId> SeedTurnAsync(MultiChannelAgentDbContext db, string nativeMessageId)
    {
        var turnId = TurnId.NewId();

        if (!await db.Participants.AnyAsync(p => p.Id == Participant.Value))
        {
            db.Participants.Add(new ParticipantEntity
            {
                Id = Participant.Value,
                DisplayName = "Streaming Participant",
                CreatedAt = Started,
                UpdatedAt = Started,
            });
        }

        db.InboxEntries.Add(new InboxEntryEntity
        {
            TurnId = turnId.Value,
            NativeMessageId = nativeMessageId,
            ParticipantId = Participant.Value,
            ChannelConversationId = "conversation-1",
            ConversationSequence = await db.InboxEntries.CountAsync() + 1,
            Channel = "web",
            PrincipalKind = ChannelPrincipalKind.EntraUser,
            PrincipalSubject = Participant.Value.ToString(),
            PrincipalTenantId = "tenant-1",
            Capabilities = ChannelCapabilities.Text,
            Locale = null,
            TraceId = null,
            WasInterrupted = false,
            ReceivedAt = Started,
            ReceivedAtTicks = Started.UtcTicks,
            CreatedAt = Started,
            Status = InboxEntryStatus.Pending,
        });

        db.InboxContentParts.Add(new InboxContentPartEntity
        {
            TurnId = turnId.Value,
            Order = 1,
            Provenance = ContentProvenance.Direct,
            Text = "hello",
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return turnId;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SqlTurnProgressEventStoreTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'SqlTurnProgressEventStore' could not be found`.

- [ ] **Step 3: Write the entity and its configuration**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/TurnProgressEventEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Turn stream progress event. Its key is the event's own issued identity -
/// the Turn plus the fixed sequence its kind is always emitted at - which is what makes appending
/// idempotent without reading a counter first, and therefore free of the read-then-write race an
/// assigned sequence would have.
/// </summary>
public sealed class TurnProgressEventEntity
{
    public Guid TurnId { get; set; }

    public long Sequence { get; set; }

    public required string Kind { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// When this marker stops being retained, as UTC ticks. Stored as ticks rather than a timestamp
    /// type for the same reason every other sweep in this model does: the comparison must translate
    /// on every relational provider these tests and production run on.
    /// </summary>
    public long ExpiresAtTicks { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/TurnProgressEventEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class TurnProgressEventEntityConfiguration : IEntityTypeConfiguration<TurnProgressEventEntity>
{
    public void Configure(EntityTypeBuilder<TurnProgressEventEntity> builder)
    {
        builder.ToTable("TurnProgressEvents");

        // The event's own identity is the key. That is the whole idempotency mechanism: a retried
        // append cannot commit a second marker, and two replicas racing the same append resolve at
        // the database rather than by either of them looking first.
        builder.HasKey(e => new { e.TurnId, e.Sequence });

        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();

        // Lets a retention pass find exactly the expired markers without scanning every one ever
        // written, by the same key the sweep orders and compares on.
        builder.HasIndex(e => e.ExpiresAtTicks);

        // A progress event may only exist for a durably accepted Turn, and it must not outlive it.
        builder.HasOne<InboxEntryEntity>()
            .WithMany()
            .HasForeignKey(e => e.TurnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

In `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`, add the set immediately after the `Deliveries` set:

```csharp
    public DbSet<TurnProgressEventEntity> TurnProgressEvents => Set<TurnProgressEventEntity>();
```

- [ ] **Step 4: Write the store**

Create `src/MultiChannelAgent.Infrastructure/Turns/SqlTurnProgressEventStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL Server-backed <see cref="ITurnProgressEventStore"/>. Idempotency is enforced by the
/// (TurnId, Sequence) primary key rather than by looking first, so two replicas racing the same
/// append resolve at the database and the loser converges on "already recorded" instead of leaking a
/// raw <see cref="DbUpdateException"/> - the same resolution <see cref="SqlInboxStore.AcceptAsync"/>
/// and <see cref="SqlFoundryConversationBindingStore.GetOrCreateAsync"/> already use.
/// </summary>
public sealed class SqlTurnProgressEventStore(MultiChannelAgentDbContext db) : ITurnProgressEventStore
{
    public async Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        db.TurnProgressEvents.Add(new TurnProgressEventEntity
        {
            TurnId = progressEvent.TurnId.Value,
            Sequence = progressEvent.Sequence,
            Kind = progressEvent.Kind.ToString(),
            OccurredAt = progressEvent.OccurredAt,
            ExpiresAtTicks = progressEvent.ExpiresAt.UtcTicks,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // A retried processing attempt, or a second replica, reaching the same identity. Clear
            // this failed attempt from the tracker - one TurnProcessingCoordinator pass shares a
            // single scoped DbContext across a whole batch, so a stale tracked entry would otherwise
            // be resent on the next Turn's save - and re-read: if the row is there now, this was that
            // race. Anything else is a real failure and must propagate untouched.
            db.ChangeTracker.Clear();

            var alreadyRecorded = await db.TurnProgressEvents
                .AsNoTracking()
                .AnyAsync(
                    e => e.TurnId == progressEvent.TurnId.Value && e.Sequence == progressEvent.Sequence,
                    cancellationToken);

            if (!alreadyRecorded)
            {
                throw;
            }

            return false;
        }
    }

    public async Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var entities = await db.TurnProgressEvents
            .AsNoTracking()
            .Where(e => e.TurnId == turnId.Value)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        return entities
            .Select(e => new TurnProgressEvent
            {
                TurnId = new TurnId(e.TurnId),
                Sequence = e.Sequence,
                Kind = Enum.Parse<TurnEventKind>(e.Kind),
                OccurredAt = e.OccurredAt,
                ExpiresAt = new DateTimeOffset(e.ExpiresAtTicks, TimeSpan.Zero),
            })
            .ToList();
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken)
    {
        var nowTicks = now.UtcTicks;

        // The bounded set is selected first so one pass can never turn into an unbounded delete:
        // ordering and bounding inside ExecuteDelete is not translatable on every provider this model
        // runs on.
        var expiring = await db.TurnProgressEvents
            .AsNoTracking()
            .Where(e => e.ExpiresAtTicks <= nowTicks)
            .OrderBy(e => e.ExpiresAtTicks)
            .ThenBy(e => e.TurnId)
            .ThenBy(e => e.Sequence)
            .Take(maxCount)
            .Select(e => new { e.TurnId, e.Sequence })
            .ToListAsync(cancellationToken);

        if (expiring.Count == 0)
        {
            return 0;
        }

        // Deleted by the identities that were actually selected, one set-based statement per distinct
        // sequence in the batch. The obvious single statement - "any selected TurnId AND any selected
        // Sequence" - would delete the CROSS PRODUCT of those two sets, which is both wrong and
        // unbounded in exactly the way maxCount exists to prevent. Grouping keeps every statement
        // exact, and the number of statements is bounded by how many distinct progress identities one
        // batch can contain, which is a small constant.
        var deleted = 0;
        foreach (var group in expiring.GroupBy(e => e.Sequence))
        {
            var sequence = group.Key;
            var turnIds = group.Select(e => e.TurnId).ToList();

            deleted += await db.TurnProgressEvents
                .Where(e => e.Sequence == sequence && turnIds.Contains(e.TurnId) && e.ExpiresAtTicks <= nowTicks)
                .ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }
}
```

- [ ] **Step 5: Register the store**

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add immediately after the `ITurnResultStore` registration:

```csharp
        services.AddScoped<ITurnProgressEventStore, SqlTurnProgressEventStore>();
```

and immediately after the `TurnOutcomeReader` registration:

```csharp
        services.AddScoped<TurnEventReader>();
```

- [ ] **Step 6: Generate the migration**

```bash
dotnet tool install --global dotnet-ef --version 10.0.11 || true
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddTurnProgressEvents \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure
```

Open the generated `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/*_AddTurnProgressEvents.cs` and confirm `Up` creates exactly the `TurnProgressEvents` table with a composite `(TurnId, Sequence)` primary key, a cascading foreign key to `InboxEntries`, and `IX_TurnProgressEvents_ExpiresAtTicks`; and that `Down` drops only that table. If it contains anything else, the model has drifted - fix the drift rather than editing the migration.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SqlTurnProgressEventStoreTests`
Expected: PASS, 6 tests.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure src/MultiChannelAgent.Application tests/MultiChannelAgent.IntegrationTests/SqlTurnProgressEventStoreTests.cs
git commit -m "feat: persist Turn stream progress events idempotently by their issued identity"
```

---

## Task 5: Retain progress events for exactly as long as the answer's payload

**Files:**
- Create: `src/MultiChannelAgent.Application/Turns/TurnProgressEventCleanupCoordinator.cs`
- Create: `src/MultiChannelAgent.Host/Workers/TurnProgressEventCleanupWorker.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Turns/TurnProgressEventCleanupCoordinatorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Turns/TurnProgressEventCleanupCoordinatorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Turns;

/// <summary>
/// The scheduled sweep that keeps the progress log bounded. It is leased, like every other periodic
/// pass in this system, so several hosted replicas never duplicate the work, and one-shot so a test
/// drives it deterministically instead of timing a background loop.
/// </summary>
public class TurnProgressEventCleanupCoordinatorTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTurnProgressEventStore _progressEvents = new();
    private readonly InMemoryLeaseCoordinator _leases = new();
    private readonly FakeTimeProvider _time = new(Started);

    private TurnProgressEventCleanupCoordinator Coordinator => new(
        _progressEvents, _leases, _time, NullLogger<TurnProgressEventCleanupCoordinator>.Instance);

    [Fact]
    public async Task A_pass_discards_every_marker_whose_retention_has_passed()
    {
        await _progressEvents.AppendAsync(TurnProgressEvent.Processing(TurnId.NewId(), Started), CancellationToken.None);
        await _progressEvents.AppendAsync(TurnProgressEvent.Processing(TurnId.NewId(), Started), CancellationToken.None);

        _time.Advance(TurnProgressEvent.Retention + TimeSpan.FromMinutes(1));

        Assert.Equal(2, await Coordinator.PurgeExpiredProgressAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_marker_still_inside_its_retention_window_is_left_alone()
    {
        var turnId = TurnId.NewId();
        await _progressEvents.AppendAsync(TurnProgressEvent.Processing(turnId, Started), CancellationToken.None);

        _time.Advance(TurnProgressEvent.Retention - TimeSpan.FromMinutes(1));

        Assert.Equal(0, await Coordinator.PurgeExpiredProgressAsync(CancellationToken.None));
        Assert.Single(await _progressEvents.ReadAsync(turnId, CancellationToken.None));
    }

    [Fact]
    public async Task A_replica_that_cannot_take_the_lease_does_no_work_at_all()
    {
        await _progressEvents.AppendAsync(TurnProgressEvent.Processing(TurnId.NewId(), Started), CancellationToken.None);
        _time.Advance(TurnProgressEvent.Retention + TimeSpan.FromMinutes(1));

        await using var held = await _leases.TryAcquireAsync(
            "turn-progress-cleanup", "someone-else", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(held);

        Assert.Equal(0, await Coordinator.PurgeExpiredProgressAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnProgressEventCleanupCoordinatorTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'TurnProgressEventCleanupCoordinator' could not be found`.

- [ ] **Step 3: Write the coordinator**

Create `src/MultiChannelAgent.Application/Turns/TurnProgressEventCleanupCoordinator.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Discards Turn stream progress markers once their retention has passed. A marker exists only so a
/// Participant reconnecting to an in-flight answer can see that work started; once the answer itself
/// is permanent it says nothing, and without a scheduled sweep it would accumulate for the life of
/// the database. Only the marker is dropped - the Turn, its Outcome, and its Deliveries are
/// untouched, so a Turn never stops having an answer.
///
/// Runs under its own exclusive lease, so several hosted replicas never duplicate the work, and
/// exposes a deterministic one-shot operation so tests can drive it without timing a background loop.
/// </summary>
public sealed class TurnProgressEventCleanupCoordinator(
    ITurnProgressEventStore progressEventStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<TurnProgressEventCleanupCoordinator> logger)
{
    private const string LeaseName = "turn-progress-cleanup";

    /// <summary>Bounds one pass so a large backlog is drained over several passes instead of one long transaction.</summary>
    private const int MaxBatchSize = 500;

    public async Task<int> PurgeExpiredProgressAsync(CancellationToken cancellationToken)
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

        var purgedCount = await progressEventStore.DeleteExpiredAsync(
            timeProvider.GetUtcNow(), MaxBatchSize, cancellationToken);

        if (purgedCount > 0)
        {
            logger.LogInformation("Discarded {PurgedCount} expired Turn progress events.", purgedCount);
        }

        return purgedCount;
    }
}
```

- [ ] **Step 4: Write the worker and register both**

Create `src/MultiChannelAgent.Host/Workers/TurnProgressEventCleanupWorker.cs`:

```csharp
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="TurnProgressEventCleanupCoordinator.PurgeExpiredProgressAsync"/>,
/// so Turn stream progress markers are discarded once they expire instead of accumulating for the
/// life of the database. It runs on the same fifteen-minute period as the Outcome payload sweep,
/// because both retain for the same twenty-four hours.
/// </summary>
public sealed class TurnProgressEventCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<TurnProgressEventCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<TurnProgressEventCleanupCoordinator>();
                await coordinator.PurgeExpiredProgressAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "A Turn progress event cleanup pass failed.");
            }
        }
    }
}
```

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add immediately after the `OutcomePayloadCleanupCoordinator` registration:

```csharp
        services.AddScoped<TurnProgressEventCleanupCoordinator>();
```

In `src/MultiChannelAgent.Host/Program.cs`, add immediately after `builder.Services.AddHostedService<OutcomePayloadCleanupWorker>();`:

```csharp
builder.Services.AddHostedService<TurnProgressEventCleanupWorker>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnProgressEventCleanupCoordinatorTests`
Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/TurnProgressEventCleanupCoordinator.cs \
        src/MultiChannelAgent.Host/Workers/TurnProgressEventCleanupWorker.cs \
        src/MultiChannelAgent.Host/Program.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        tests/MultiChannelAgent.Application.Tests/Turns/TurnProgressEventCleanupCoordinatorTests.cs
git commit -m "feat: sweep expired Turn progress events on the Outcome payload retention window"
```

---

## Task 6: The finite resumable per-Turn SSE endpoint

**Files:**
- Create: `src/MultiChannelAgent.Host/Endpoints/ServerSentEvents.cs`
- Create: `src/MultiChannelAgent.Host/Endpoints/TurnEventStreamResult.cs`
- Modify: `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/ServerSentEventReader.cs`
- Modify: `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/TurnEventStreamHttpTests.cs`

- [ ] **Step 1: Write the test reader helper**

Create `tests/MultiChannelAgent.IntegrationTests/ServerSentEventReader.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>One decoded Server-Sent Event: its issued identity (absent on a stream that issues none), its name, and its body.</summary>
public sealed record ServerSentEvent(long? Id, string Name, string Data);

/// <summary>
/// Decodes a live <c>text/event-stream</c> response the way a browser's EventSource does: field by
/// field, dispatching on the blank line that terminates a record, and skipping comment lines. Tests
/// need this because <c>HttpClient</c> gives them bytes, not events, and because asserting on raw
/// bytes would couple every test to the exact framing instead of to the events themselves.
///
/// It is deliberately <b>stateful</b>, and owns one reader over the response body for its whole life.
/// A stateless "read N events out of this response" helper cannot be written correctly:
/// <see cref="StreamReader"/> reads ahead into its own buffer, so bytes already pulled off the socket
/// but not yet returned are thrown away when it is disposed - and a second call on the same response
/// would either read from a body the first call had already disposed, or silently skip whatever the
/// first call had buffered. One reader, opened once, read repeatedly, disposed at the end, is the only
/// shape in which a test can read some events, do something to the system, and then read the rest.
///
/// Being stateful is not enough on its own, and the second half of the contract is what makes the
/// first half true: <b>every</b> byte is consumed through the one <c>PumpAsync</c> state machine, and
/// a completed event is <b>queued</b> rather than returned directly. Both consequences matter.
/// The half-parsed record left behind when a read stops mid-event survives into the next call,
/// because the fields it has already seen are instance state rather than locals. And an event that
/// arrives while a caller is waiting for a heartbeat is queued for the next <see cref="ReadAsync"/>
/// instead of being decoded and dropped - which is what a separate line-skipping heartbeat loop would
/// do, silently, in exactly the tests written to prove nothing is lost.
///
/// The caller keeps owning the <see cref="HttpResponseMessage"/>; this owns only what it created.
/// </summary>
public sealed class ServerSentEventReader : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;

    /// <summary>Events fully decoded but not yet handed to a caller - including any decoded while waiting for a heartbeat.</summary>
    private readonly Queue<ServerSentEvent> _decoded = new();

    // The record currently being decoded. Instance state, not locals, because one SSE record can
    // straddle two calls: a read that has satisfied its count mid-record, or a heartbeat wait that
    // stops between an event's "id:" line and its terminating blank line.
    private readonly StringBuilder _data = new();
    private long? _id;
    private string? _name;
    private bool _ended;

    private ServerSentEventReader(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8);
    }

    /// <summary>How many comment lines - the keep-alive heartbeats - this reader has passed over so far.</summary>
    public int HeartbeatCount { get; private set; }

    /// <summary>Opens a reader over a live streaming response.</summary>
    public static async Task<ServerSentEventReader> OpenAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new ServerSentEventReader(await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    /// <summary>
    /// Reads events until <paramref name="count"/> have arrived or the server ends the stream. Never
    /// blocks forever: <paramref name="cancellationToken"/> is the test's own timeout. A second call
    /// continues exactly where the previous one stopped, and anything decoded during a
    /// <see cref="WaitForHeartbeatsAsync"/> is returned here rather than lost.
    /// </summary>
    public async Task<IReadOnlyList<ServerSentEvent>> ReadAsync(int count, CancellationToken cancellationToken)
    {
        var events = new List<ServerSentEvent>();

        while (events.Count < count)
        {
            if (_decoded.Count > 0)
            {
                events.Add(_decoded.Dequeue());
                continue;
            }

            if (!await PumpAsync(cancellationToken))
            {
                break;
            }
        }

        return events;
    }

    /// <summary>
    /// Reads until at least <paramref name="count"/> comment lines have been passed over, or the
    /// stream ends. A comment carries no identity and no body, so it is invisible to
    /// <see cref="ReadAsync"/>; this is how a test asserts the keep-alive an ingress depends on is
    /// actually being written. It consumes the stream through the same state machine
    /// <see cref="ReadAsync"/> does, so any event that arrives while it is waiting is decoded and
    /// queued for the next <see cref="ReadAsync"/> rather than discarded.
    /// </summary>
    public async Task WaitForHeartbeatsAsync(int count, CancellationToken cancellationToken)
    {
        while (HeartbeatCount < count)
        {
            if (!await PumpAsync(cancellationToken))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Consumes exactly one line and applies it to the decode state, queueing an event when the line
    /// terminates one. Returns false once the server has ended the stream, and never reads past that.
    /// This is the single place bytes are interpreted, which is what stops the two public waits from
    /// disagreeing about what a byte meant.
    /// </summary>
    private async Task<bool> PumpAsync(CancellationToken cancellationToken)
    {
        if (_ended)
        {
            return false;
        }

        var line = await _reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            _ended = true;
            return false;
        }

        if (line.Length == 0)
        {
            if (_name is not null)
            {
                _decoded.Enqueue(new ServerSentEvent(_id, _name, _data.ToString()));
            }

            _id = null;
            _name = null;
            _data.Clear();
            return true;
        }

        if (line.StartsWith(':'))
        {
            HeartbeatCount++;
            return true;
        }

        var separator = line.IndexOf(':');
        var field = separator < 0 ? line : line[..separator];
        var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');

        switch (field)
        {
            case "id":
                _id = long.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "event":
                _name = value;
                break;
            case "data":
                _data.Append(value);
                break;
            default:
                break;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
    }
}
```

- [ ] **Step 2: Add the client helpers**

In `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs`, the private `CookieJar _jar = new();` field must become one a second tab can share. Replace the field and the private constructor with:

```csharp
    private readonly HttpClient _client;
    private readonly CookieJar _jar;

    private ConversationTestClient(HttpClient client, CookieJar jar)
    {
        _client = client;
        _jar = jar;
    }
```

and change the one construction site inside `SignInAsync` to `new ConversationTestClient(client, new CookieJar())`.

`CsrfToken` and `ParticipantIdentifier` are `{ get; private set; }`, so a second tab copies them through a private helper rather than an object initializer. Add both members to the class:

```csharp
    /// <summary>
    /// A second browser tab of the same browser profile: the same cookie jar (therefore the same
    /// authenticated session AND the same web ChannelConversation cookie), the same CSRF token, and
    /// the same Participant. This is what makes "one browser-profile conversation shared across tabs"
    /// testable rather than assumed.
    /// </summary>
    public ConversationTestClient OpenAnotherTab() => new ConversationTestClient(_client, _jar).WithIdentityOf(this);

    private ConversationTestClient WithIdentityOf(ConversationTestClient other)
    {
        CsrfToken = other.CsrfToken;
        ParticipantIdentifier = other.ParticipantIdentifier;
        return this;
    }
```

Then add the three streaming helpers:

```csharp
    /// <summary>Opens this Turn's event stream, optionally resuming after an event this client already has.</summary>
    public async Task<HttpResponseMessage> OpenTurnStreamAsync(
        Guid turnId, long? lastEventId = null, CancellationToken cancellationToken = default)
    {
        var url = lastEventId is { } resumeFrom
            ? $"/api/turns/{turnId}/events?lastEventId={resumeFrom}"
            : $"/api/turns/{turnId}/events";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        _jar.Apply(request);

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Opens this Participant's Inventory invalidation stream.</summary>
    public async Task<HttpResponseMessage> OpenInventoryStreamAsync(CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory-events");
        _jar.Apply(request);

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Rotates this conversation's Foundry history and clears its pending confirmation state.</summary>
    public async Task<HttpResponseMessage> StartNewConversationAsync() =>
        await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/conversation/new"), withCsrf: true);
```

- [ ] **Step 3: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/TurnEventStreamHttpTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Host.Endpoints;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The resumable per-Turn stream over real HTTP, backed by SQLite (fast, Docker-free). Everything
/// #35 promises about disconnecting and coming back is a property of this endpoint: the same events
/// in the same order however often you reconnect, exactly one terminal event, a stream that ends
/// itself, a resume point that is honoured, a bad resume point that is ignored rather than fatal, and
/// a Turn belonging to someone else that is indistinguishable from one that does not exist.
/// </summary>
public sealed class TurnEventStreamHttpTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        // Deliberately the real TimeProvider: the stream's poll interval, heartbeat, and interactive
        // wait bound are real delays, and a FakeTimeProvider nobody advances would hang them forever.
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_answered_turn_streams_acceptance_progress_parts_and_one_terminal_outcome_then_ends()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Streaming Participant");
        await participant.CreateAndSelectInventoryAsync("Streamed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-1", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var events = await reader.ReadAsync(5, timeout.Token);

        Assert.Equal(
            ["accepted", "processing", "part", "part", "outcome"],
            events.Select(e => e.Name));
        Assert.Equal(
            new long?[]
            {
                TurnEventSequence.Accepted,
                TurnEventSequence.Processing,
                TurnEventSequence.ForPart(1),
                TurnEventSequence.ForPart(2),
                TurnEventSequence.Outcome,
            },
            events.Select(e => e.Id));

        var terminal = JsonDocument.Parse(events[^1].Data).RootElement;
        Assert.Equal(turnId, terminal.GetProperty("turnId").GetGuid());
        Assert.Equal("completed", terminal.GetProperty("status").GetString());

        // The stream is finite: nothing follows the terminal event and the server ends the response.
        // Reading again from the SAME reader is what proves it, which is only meaningful because the
        // reader is stateful - a fresh one would have lost whatever the first read had buffered.
        Assert.Empty(await reader.ReadAsync(1, timeout.Token));
    }

    [Fact]
    public async Task A_stream_with_nothing_left_to_say_keeps_proving_it_is_still_alive()
    {
        // Everything about this factory is production except three numbers. The heartbeat has to be
        // asserted - an ingress that sees no bytes closes the connection, and a stream that stopped
        // heart-beating would fail in production and nowhere else - but asserting it at the production
        // fifteen seconds would add half a minute to every CI run forever.
        using var fastHeartbeat = new SqliteWebApplicationFactory(
            configureTestServices: services => services.AddSingleton(new TurnStreamOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
                HeartbeatInterval = TimeSpan.FromMilliseconds(200),
                MaxDuration = TimeSpan.FromSeconds(30),
            }));

        var http = ConversationTestClient.CreateHttpsClient(fastHeartbeat);
        var participant = await ConversationTestClient.SignInAsync(http, "Idle Participant");
        await participant.CreateAndSelectInventoryAsync("Idle Warehouse");

        // Deliberately never processed: a Turn with one event and then nothing at all is the only
        // state in which a keep-alive matters.
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-heartbeat", "list stock");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        // Deliberately NOT reading the `accepted` event first. The stream writes it on its first
        // poll, 50 ms in, long before the first 200 ms heartbeat - so waiting for heartbeats here
        // means the wait itself consumes that event's lines. A heartbeat wait that skipped anything
        // that was not a comment would decode it into nothing and lose it silently.
        await reader.WaitForHeartbeatsAsync(2, timeout.Token);

        // Two beats, so this cannot pass on a single accidental byte: the stream is repeating itself
        // on a timer while it has nothing to say.
        Assert.True(
            reader.HeartbeatCount >= 2,
            $"A silent stream must keep writing keep-alive comments, but only {reader.HeartbeatCount} arrived.");

        // And the event the wait ran over is still there to be read, because the wait used the same
        // parse/dispatch state machine and queued what it completed. This is the reader's whole
        // contract - sequential reads of one response lose nothing - asserted rather than assumed.
        Assert.Equal(["accepted"], (await reader.ReadAsync(1, timeout.Token)).Select(e => e.Name));
    }

    [Fact]
    public async Task Every_events_data_is_exactly_one_line_so_the_framing_can_never_be_broken_by_content()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Framing Participant");
        await participant.CreateAndSelectInventoryAsync("Framed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-frame", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        foreach (var line in body.Split('\n').Where(l => l.StartsWith("data:")))
        {
            Assert.DoesNotContain('\r', line);
            JsonDocument.Parse(line["data:".Length..].TrimStart());
        }
    }

    [Fact]
    public async Task Reconnecting_with_a_resume_point_replays_only_what_came_after_it()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Resuming Participant");
        await participant.CreateAndSelectInventoryAsync("Resumed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-2", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(
            turnId, TurnEventSequence.Processing, timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var events = await reader.ReadAsync(3, timeout.Token);

        Assert.Equal(["part", "part", "outcome"], events.Select(e => e.Name));
    }

    [Fact]
    public async Task Reconnecting_from_the_terminal_event_ends_the_stream_immediately_with_nothing_replayed()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Finished Participant");
        await participant.CreateAndSelectInventoryAsync("Finished Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-3", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, TurnEventSequence.Outcome, timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        Assert.Empty(await reader.ReadAsync(1, timeout.Token));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("999999999999999999")]
    public async Task A_resume_point_this_application_never_issued_is_ignored_and_the_whole_stream_replays(string lastEventId)
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Tampering Participant");
        await participant.CreateAndSelectInventoryAsync("Tampered Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync($"native-stream-bad-{lastEventId}", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId}/events?lastEventId={Uri.EscapeDataString(lastEventId)}");
        using var response = await participant.SendAsync(request);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        // Never an error: a browser's EventSource cannot read an error body and would reconnect
        // forever with the same bad value, so a value we never issued is treated exactly as if none
        // had been sent - the same rule WebConversationCookie applies to a tampered cookie.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["accepted", "processing", "part", "part", "outcome"],
            (await reader.ReadAsync(5, timeout.Token)).Select(e => e.Name));
    }

    [Fact]
    public async Task The_last_event_id_request_header_a_browser_sends_on_its_own_reconnect_is_honoured()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Header Participant");
        await participant.CreateAndSelectInventoryAsync("Header Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-4", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId}/events");
        request.Headers.Add("Last-Event-ID", TurnEventSequence.ForPart(1).ToString());
        using var response = await participant.SendAsync(request);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        Assert.Equal(["part", "outcome"], (await reader.ReadAsync(2, timeout.Token)).Select(e => e.Name));
    }

    [Fact]
    public async Task Another_participants_turn_and_a_turn_that_does_not_exist_are_indistinguishable()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(http, "Turn Owner");
        await owner.CreateAndSelectInventoryAsync("Private Warehouse");
        var turnId = await owner.SubmitAcceptedTurnAsync("native-stream-5", "list stock");
        await ProcessUntilQuietAsync();

        var stranger = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Turn Stranger");

        using var foreign = await stranger.OpenTurnStreamAsync(turnId);
        using var missing = await stranger.OpenTurnStreamAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_turn_that_has_not_been_processed_yet_streams_its_acceptance_and_keeps_the_connection_open()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Waiting Participant");
        await participant.CreateAndSelectInventoryAsync("Waiting Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-6", "list stock");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var reading = reader.ReadAsync(5, timeout.Token);
        await ProcessUntilQuietAsync();
        var events = await reading;

        Assert.Equal("accepted", events[0].Name);
        Assert.Equal("outcome", events[^1].Name);
    }

    [Fact]
    public async Task Disconnecting_mid_stream_changes_nothing_and_the_recorded_outcome_is_still_there_afterwards()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Disconnecting Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Disconnected Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-7", "add stock Steel Bolts quantity 4");

        using (var abort = new CancellationTokenSource())
        {
            using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: abort.Token);
            await abort.CancelAsync();
        }

        await ProcessUntilQuietAsync();

        var outcome = await participant.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);

        // Recovery is a read. Exactly one Turn was ever accepted for this native message, so nothing
        // mutation-capable was resubmitted by reconnecting or by giving up.
        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<TurnEventReader>();
        Assert.NotNull(await reader.ReadAfterAsync(
            new TurnId(turnId),
            new Domain.Inventories.ParticipantId(Guid.Parse((await participant.GetBootstrapAsync())
                .GetProperty("bootstrap").GetProperty("participantId").GetString()!)),
            0L,
            CancellationToken.None));

        Assert.NotEqual(Guid.Empty, inventoryId);
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~TurnEventStreamHttpTests`
Expected: FAIL. The first assertion to fail is `Assert.Equal(HttpStatusCode.OK, response.StatusCode)` reporting `NotFound`, because `/api/turns/{id}/events` is not mapped yet.

- [ ] **Step 5: Write the SSE wire format**

Create `src/MultiChannelAgent.Host/Endpoints/ServerSentEvents.cs`:

```csharp
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// How the per-Turn stream paces itself. Registered as a singleton in <c>Program.cs</c> holding
/// exactly the production numbers below; nothing else in the application reads them.
///
/// It is a settable record rather than three constants for one reason, and it is a test reason worth
/// being honest about: the heartbeat is load-bearing (an ingress that sees no bytes closes the
/// connection), so it has to be asserted, and asserting it at fifteen real seconds would tax every CI
/// run forever. A fake clock cannot help - these values are consumed inside a live HTTP request that
/// a test is concurrently reading bytes from, so there is no safe moment for anyone to advance one.
/// </summary>
public sealed record TurnStreamOptions
{
    /// <summary>How often the stream looks for events it has not sent yet.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the stream may stay silent before it proves it is still alive.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The bounded interactive wait. After this the client reconnects and resumes.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>How the Participant-level invalidation stream paces itself. Same rationale as <see cref="TurnStreamOptions"/>.</summary>
public sealed record InventoryStreamOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The bounded life of one connection. A browser's EventSource reconnects by itself, and a
    /// reconnect costs one snapshot, so bounding this keeps a long-lived tab's connection fresh
    /// without the client having to do anything.
    /// </summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The Server-Sent Events wire format, in one place. Every stream this application serves frames its
/// events here, so the framing rules - and the one rule that matters, that a body is always exactly
/// one line - can never drift between streams.
/// </summary>
public static class ServerSentEvents
{
    public const string ContentType = "text/event-stream";
    /// <summary>The header a browser's EventSource sets by itself when it reconnects a stream it was already reading.</summary>
    public const string LastEventIdHeader = "Last-Event-ID";

    /// <summary>
    /// The query parameter a client uses to resume a stream it is opening fresh. A browser can only
    /// set the header on its <em>own</em> automatic reconnect; a page that reloaded and is
    /// reconnecting deliberately has no way to set a header at all, so it passes its resume point
    /// here instead.
    /// </summary>
    public const string LastEventIdQuery = "lastEventId";

    public static void PrepareResponse(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache, no-store";

        // Some reverse proxies buffer responses by default, which would hold every event until the
        // stream ended and defeat the entire point of streaming.
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    /// <summary>
    /// Writes one event and flushes it. <paramref name="data"/> must already be a single line; every
    /// caller in this application produces it with <c>JsonSerializer</c>, which escapes newlines
    /// inside strings and never pretty-prints, so this holds by construction.
    /// </summary>
    public static async Task WriteEventAsync(
        HttpResponse response, long? id, string name, string data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var frame = new StringBuilder();
        if (id is { } issued)
        {
            frame.Append("id: ").Append(issued.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        frame.Append("event: ").Append(name).Append('\n');
        frame.Append("data: ").Append(data).Append("\n\n");

        await response.WriteAsync(frame.ToString(), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a comment line. Necessary rather than decorative: an ingress that sees no bytes for
    /// long enough closes the connection. A comment carries no identity, so it can never move a
    /// client's resume point.
    /// </summary>
    public static async Task WriteHeartbeatAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        await response.WriteAsync(": heartbeat\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// The resume point this request is asking for, or 0 for "from the beginning". Anything this
    /// application did not issue - unparseable, negative, or simply not one of its identities - is
    /// treated exactly as if none had been sent, and never as an error: a browser's EventSource
    /// cannot read an error body, so refusing would make it reconnect forever with the same bad
    /// value, and replaying a caller's own events discloses nothing they did not already have.
    /// </summary>
    public static long ReadResumePoint(HttpRequest request, Func<long, bool> isIssued)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(isIssued);

        var raw = request.Headers[LastEventIdHeader].FirstOrDefault()
            ?? request.Query[LastEventIdQuery].FirstOrDefault();

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && isIssued(value)
            ? value
            : 0L;
    }
}
```

- [ ] **Step 6: Write the stream result**

Create `src/MultiChannelAgent.Host/Endpoints/TurnEventStreamResult.cs`:

```csharp
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// One Turn's finite, resumable event stream.
///
/// It is finite in two ways. It ends as soon as the terminal Outcome has been written, because
/// nothing can follow it. And it ends anyway after <see cref="MaxDuration"/>, because an interactive
/// wait has to be bounded: a client whose Turn is still running simply reconnects with the identity
/// of the last event it received and carries on exactly where it left off.
///
/// It polls, in a fresh dependency scope each pass, rather than waiting on an in-process signal. Two
/// reasons, both structural: this application runs as several replicas, so the replica processing a
/// Turn is routinely not the one holding its stream; and a five-minute request must never hold one
/// database context open for its whole life.
///
/// It is a read and nothing else. A disconnect therefore cancels it and undoes nothing, which is
/// precisely what lets a Participant reconnect to mutation-capable work without ever resubmitting it.
/// </summary>
public sealed class TurnEventStreamResult(
    TurnId turnId, ParticipantId participantId, long resumePoint, TurnEventPage firstPage) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
        var options = httpContext.RequestServices.GetRequiredService<TurnStreamOptions>();
        var cancellationToken = httpContext.RequestAborted;

        ServerSentEvents.PrepareResponse(httpContext.Response);

        var sent = resumePoint;
        var deadline = timeProvider.GetUtcNow() + options.MaxDuration;
        var lastWrite = timeProvider.GetUtcNow();
        var page = firstPage;

        try
        {
            while (true)
            {
                foreach (var streamEvent in page.Events)
                {
                    await ServerSentEvents.WriteEventAsync(
                        httpContext.Response, streamEvent.Sequence, streamEvent.Name, streamEvent.Data, cancellationToken);
                    sent = streamEvent.Sequence;
                    lastWrite = timeProvider.GetUtcNow();
                }

                if (page.ReachedTerminal || timeProvider.GetUtcNow() >= deadline)
                {
                    return;
                }

                if (timeProvider.GetUtcNow() - lastWrite >= options.HeartbeatInterval)
                {
                    await ServerSentEvents.WriteHeartbeatAsync(httpContext.Response, cancellationToken);
                    lastWrite = timeProvider.GetUtcNow();
                }

                await Task.Delay(options.PollInterval, timeProvider, cancellationToken);

                using var scope = scopeFactory.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<TurnEventReader>();

                // A Turn cannot stop existing or change owner, so a null here is unreachable; treating
                // it as "finished" simply ends the stream rather than looping forever if it ever did.
                page = await reader.ReadAfterAsync(turnId, participantId, sent, cancellationToken)
                    ?? new TurnEventPage([], ReachedTerminal: true);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away, refreshed, or lost its connection. There is nothing to undo:
            // this endpoint only ever reads, so the Turn is exactly where it was and the Participant
            // can pick it back up by reconnecting.
        }
    }
}
```

- [ ] **Step 7: Register the stream timings and map the endpoint**

In `src/MultiChannelAgent.Host/Program.cs`, add immediately after `builder.Services.AddHostedService<TurnProgressEventCleanupWorker>();`:

```csharp
// The production numbers, in one place. A test that must not wait fifteen real seconds for a
// heartbeat replaces this one registration and changes nothing else.
builder.Services.AddSingleton(new TurnStreamOptions());
builder.Services.AddSingleton(new InventoryStreamOptions());
```

In `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs`, add immediately after the existing `group.MapGet("/{turnId:guid}/outcome", ...)` registration and before `return endpoints;`:

```csharp
        group.MapGet("/{turnId:guid}/events", async (
            Guid turnId,
            HttpContext httpContext,
            ClaimsPrincipal user,
            TurnEventReader eventReader,
            CancellationToken cancellationToken) =>
        {
            var participantId = user.GetParticipantId();
            var resumePoint = ServerSentEvents.ReadResumePoint(httpContext.Request, TurnEventSequence.IsIssued);

            // The first page is read before any streaming header is written, so a Turn that does not
            // exist - or belongs to a different Participant - can still be answered with a plain 404,
            // identical in both cases exactly as the Outcome endpoint answers them.
            var firstPage = await eventReader.ReadAfterAsync(new TurnId(turnId), participantId, resumePoint, cancellationToken);

            return firstPage is null
                ? Results.NotFound()
                : new TurnEventStreamResult(new TurnId(turnId), participantId, resumePoint, firstPage);
        });
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~TurnEventStreamHttpTests`
Expected: PASS, 10 tests (the `[Theory]` contributes 4 cases, so 13 test results).

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Host/Endpoints src/MultiChannelAgent.Host/Program.cs tests/MultiChannelAgent.IntegrationTests
git commit -m "feat: serve a finite resumable per-Turn SSE stream with issued event ids"
```

---

## Task 7: Publish an Inventory version bump from the persistence seam

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InventoryVersionEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/InventoryVersionEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Application/Inventories/IInventoryVersionStore.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryVersionStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Generate: `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/*_AddInventoryVersions.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/InventoryVersionBumpTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/InventoryVersionBumpTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage of the one seam that publishes "something in this Inventory changed".
///
/// It is deliberately not a call any endpoint or store makes. It keys off the minimal semantic audit
/// fact every state-changing store already stages in the same save, which is what makes it impossible
/// for a future mutation path - or a future channel - to change Inventory state without publishing:
/// forgetting to publish would mean forgetting to audit, which is a far louder failure. Because the
/// bump runs inside the caller's own transaction, and always last, nothing is ever published before
/// it commits, a rollback takes the version with it, and the version row's lock is held for the
/// shortest possible slice of the transaction. It is not a deadlock-prevention scheme and is not
/// claimed to be one.
/// </summary>
public sealed class InventoryVersionBumpTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Actor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();

    public InventoryVersionBumpTests()
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
    public async Task A_new_inventory_starts_at_version_zero_without_anyone_asking_for_it()
    {
        using var db = CreateContext();

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task One_audited_change_moves_the_inventory_forward_exactly_one_version()
    {
        using var db = CreateContext();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task Several_audit_facts_committed_together_still_move_it_forward_exactly_once()
    {
        using var db = CreateContext();

        db.InventoryAudits.Add(Audit(AuditEventType.StockAdded));
        db.InventoryAudits.Add(Audit(AuditEventType.StockRemoved));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The signal is "refetch this Inventory", not "here is a change log", so one commit is one
        // version however many facts it recorded.
        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_denied_access_attempt_changes_nothing_and_therefore_publishes_nothing()
    {
        using var db = CreateContext();

        await RecordAuditAsync(db, AuditEventType.AccessDenied);

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_save_that_records_no_audit_fact_publishes_nothing()
    {
        using var db = CreateContext();

        db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
            RetiredAt = null,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_change_that_rolls_back_never_leaves_a_published_version_behind()
    {
        using var db = CreateContext();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RecordAuditAsync(db, AuditEventType.StockAdded);
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();

        // Nothing was published before the commit, because the bump ran inside the very transaction
        // that was thrown away.
        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task Two_inventories_advance_independently()
    {
        var otherInventoryId = Guid.NewGuid();

        using var db = CreateContext();
        db.Inventories.Add(new InventoryEntity
        {
            Id = otherInventoryId,
            Name = "Other Warehouse",
            NormalizedName = "other warehouse",
            CreatedByParticipantId = Actor.Value,
            ClientRequestId = "seed-2",
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
        Assert.Equal(0L, await VersionAsync(db, otherInventoryId));
    }

    [Fact]
    public async Task The_store_reads_every_requested_inventory_and_omits_the_ones_it_has_no_row_for()
    {
        using var db = CreateContext();
        await RecordAuditAsync(db, AuditEventType.StockAdded);

        var versions = await new SqlInventoryVersionStore(db)
            .ReadAsync([_inventoryId, Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(1L, versions[_inventoryId]);
        Assert.Single(versions);
    }

    [Fact]
    public void The_version_row_is_referentially_independent_of_the_inventory_it_names()
    {
        using var db = CreateContext();

        // Asserted, not assumed. An audit fact about an Inventory deliberately carries no foreign key
        // (see InventoryAuditEntityConfiguration), and this row is published from exactly those facts,
        // so a cascading key here would let a state the audit model tolerates fail somebody else's
        // mutating transaction through the fallback insertion below.
        var entityType = db.Model.FindEntityType(typeof(InventoryVersionEntity))!;

        Assert.Empty(entityType.GetForeignKeys());
        Assert.Equal("InventoryVersions", entityType.GetTableName());
    }

    [Fact]
    public async Task An_inventory_that_somehow_has_no_version_row_gets_one_from_its_next_audited_change()
    {
        using var db = CreateContext();

        // Exactly the residue the migration's backfill exists to prevent, forced here so the guarded
        // fallback is a tested path rather than a hopeful comment. It is reachable at all only because
        // there is no foreign key stopping the row from being established on demand.
        await db.Database.ExecuteSqlAsync($"DELETE FROM InventoryVersions WHERE InventoryId = {_inventoryId}");
        db.ChangeTracker.Clear();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    private static async Task<long> VersionAsync(MultiChannelAgentDbContext db, Guid inventoryId)
    {
        var row = await db.InventoryVersions.AsNoTracking().FirstOrDefaultAsync(v => v.InventoryId == inventoryId);
        return row?.Version ?? -1L;
    }

    private async Task RecordAuditAsync(MultiChannelAgentDbContext db, AuditEventType eventType)
    {
        db.InventoryAudits.Add(Audit(eventType));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private InventoryAuditEntity Audit(AuditEventType eventType) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType.ToString(),
        ActorKind = AuditActorKind.Participant.ToString(),
        ActorId = Actor.Value.ToString(),
        InventoryId = _inventoryId,
        SubjectParticipantId = null,
        OutcomeCode = "ok",
        OccurredAtUtc = Now,
        OccurredAtUtcTicks = Now.UtcTicks,
        ExpiresAtUtc = Now.AddDays(90),
    };

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = Actor.Value,
            DisplayName = "Version Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = Actor.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~InventoryVersionBumpTests`
Expected: FAIL to compile with `CS1061: 'MultiChannelAgentDbContext' does not contain a definition for 'InventoryVersions'`.

- [ ] **Step 3: Write the entity and its configuration**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InventoryVersionEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One Inventory's monotonic change version - the whole durable state behind "something you are
/// looking at changed, refetch it".
///
/// Deliberately a counter and not a timestamp, and deliberately without one: an invalidation signal
/// is compared for inequality, never for age, so a clock would only add a column that could disagree
/// with itself across replicas and would have to be threaded through a DbContext that has no
/// business knowing the time.
/// </summary>
public sealed class InventoryVersionEntity
{
    public Guid InventoryId { get; set; }

    public long Version { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/InventoryVersionEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InventoryVersionEntityConfiguration : IEntityTypeConfiguration<InventoryVersionEntity>
{
    public void Configure(EntityTypeBuilder<InventoryVersionEntity> builder)
    {
        builder.ToTable("InventoryVersions");

        // One row per Inventory, keyed by it. That is what makes the bump a single atomic
        // "Version = Version + 1 WHERE InventoryId = @id" with no read, and therefore free of the
        // read-then-write race a counter read would have.
        builder.HasKey(e => e.InventoryId);

        builder.Property(e => e.InventoryId).ValueGeneratedNever();

        // Deliberately NO foreign key to Inventories, for exactly the reason
        // InventoryAuditEntityConfiguration gives for the audit rows this seam keys off: a fact about
        // an Inventory must stay independent of later changes to (or retirement of) the row it names.
        // A cascading foreign key here would also make the bump's fallback insertion - the guarded
        // path for an Inventory that somehow has no version row - able to fail a foreign key check
        // inside somebody else's mutating transaction, turning a state the audit model tolerates into
        // a hard failure of an unrelated write. Consistency is established by the two mechanisms that
        // actually establish it: this migration backfills every existing Inventory, and
        // MultiChannelAgentDbContext seeds a row for every new one in the same save.
    }
}
```

- [ ] **Step 4: Write the publish seam**

In `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`, add the using directives `using MultiChannelAgent.Domain.Inventories;` and add the set next to the other Inventory sets:

```csharp
    public DbSet<InventoryVersionEntity> InventoryVersions => Set<InventoryVersionEntity>();
```

Then add these members immediately after `OnModelCreating`:

```csharp
    /// <summary>
    /// The one place this application publishes "something in this Inventory changed".
    ///
    /// It is a save-time seam rather than a call each store makes, because a call each store makes is
    /// a call a future store can forget. Every write that changes Inventory-visible state already
    /// stages a minimal semantic <see cref="InventoryAuditEntity"/> in the same save, so keying off
    /// that means forgetting to publish would require forgetting to audit - a far louder failure, and
    /// one the governance tests already catch. <see cref="AuditEventType.AccessDenied"/> is excluded
    /// because a refused request changes nothing there is anything to refetch for.
    ///
    /// The bump runs INSIDE the caller's transaction, and always LAST. Inside, so nothing is ever
    /// published before the change it announces commits, and a rollback takes the version with it.
    /// Last, so the version row's exclusive lock is taken as late as possible and released at commit -
    /// the shortest hold this design can have. That is commit coupling and a short lock, and it is
    /// deliberately NOT claimed to be anything more: it does not serialize the work earlier in those
    /// transactions, and it prevents no deadlock that could already happen on the rows they were
    /// changing. No writer takes this lock first, and nothing here depends on one doing so.
    /// </summary>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StageVersionRowsForNewInventories();

        var inventoriesToPublish = InventoriesWithStagedChanges();
        if (inventoriesToPublish.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var ownedTransaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            foreach (var inventoryId in inventoriesToPublish)
            {
                var bumped = await Database.ExecuteSqlAsync(
                    $"UPDATE InventoryVersions SET Version = Version + 1 WHERE InventoryId = {inventoryId}",
                    cancellationToken);

                if (bumped == 0)
                {
                    // Only reachable for an Inventory created before this table existed and somehow
                    // missed by the backfill. Establishing the row here keeps the signal correct
                    // rather than silently never publishing for that Inventory again.
                    await Database.ExecuteSqlAsync(
                        $"INSERT INTO InventoryVersions (InventoryId, Version) VALUES ({inventoryId}, 1)",
                        cancellationToken);
                }
            }

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return saved;
        }
        catch
        {
            // The staged entities were either never written or have been rolled back with the
            // transaction, so leaving them tracked would resend them on the next save against this
            // same context - which one processing pass shares across a whole batch of work.
            ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The synchronous twin of <see cref="SaveChangesAsync(bool, CancellationToken)"/>. It exists so
    /// the publish seam cannot be bypassed simply by saving synchronously; production code saves
    /// asynchronously, but a seam with a hole in it is not a seam.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageVersionRowsForNewInventories();

        var inventoriesToPublish = InventoriesWithStagedChanges();
        if (inventoriesToPublish.Count == 0)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var ownedTransaction = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;

        try
        {
            var saved = base.SaveChanges(acceptAllChangesOnSuccess);

            foreach (var inventoryId in inventoriesToPublish)
            {
                var bumped = Database.ExecuteSql(
                    $"UPDATE InventoryVersions SET Version = Version + 1 WHERE InventoryId = {inventoryId}");

                if (bumped == 0)
                {
                    Database.ExecuteSql(
                        $"INSERT INTO InventoryVersions (InventoryId, Version) VALUES ({inventoryId}, 1)");
                }
            }

            ownedTransaction?.Commit();

            return saved;
        }
        catch
        {
            ChangeTracker.Clear();
            throw;
        }
        finally
        {
            ownedTransaction?.Dispose();
        }
    }

    /// <summary>
    /// Gives every Inventory being created its starting version in the same save, so the bump above
    /// is always an update of a row that exists. An Inventory's own creation writes no audit fact, so
    /// it starts at zero and is first reported the moment it appears in a Participant's authorized
    /// set.
    /// </summary>
    private void StageVersionRowsForNewInventories()
    {
        var newInventoryIds = ChangeTracker.Entries<InventoryEntity>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.Id)
            .ToList();

        if (newInventoryIds.Count == 0)
        {
            return;
        }

        var alreadyStaged = ChangeTracker.Entries<InventoryVersionEntity>()
            .Select(entry => entry.Entity.InventoryId)
            .ToHashSet();

        foreach (var inventoryId in newInventoryIds.Where(id => !alreadyStaged.Contains(id)))
        {
            InventoryVersions.Add(new InventoryVersionEntity { InventoryId = inventoryId, Version = 0L });
        }
    }

    private List<Guid> InventoriesWithStagedChanges() =>
        ChangeTracker.Entries<InventoryAuditEntity>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .Where(audit => audit.EventType != nameof(AuditEventType.AccessDenied))
            .Select(audit => audit.InventoryId)
            .Distinct()
            .ToList();
```

- [ ] **Step 5: Write the read seam**

Create `src/MultiChannelAgent.Application/Inventories/IInventoryVersionStore.cs`:

```csharp
namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Reads the current change version of Inventories. Versions are published by the persistence seam
/// itself, inside the very transaction that changes state, so there is deliberately no write method
/// here: nothing may ever bump a version without also making the change it announces.
/// </summary>
public interface IInventoryVersionStore
{
    /// <summary>
    /// The current version of each requested Inventory. An Inventory with no recorded version is
    /// simply absent from the result - callers treat that as version zero rather than as an error,
    /// because "never changed" and "changed zero times" are the same thing.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, long>> ReadAsync(
        IReadOnlyCollection<Guid> inventoryIds, CancellationToken cancellationToken);
}
```

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryVersionStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL-backed <see cref="IInventoryVersionStore"/>. One query for the whole requested set rather than
/// one per Inventory, because a Participant-level stream re-reads every Inventory they may see on
/// every poll.
/// </summary>
public sealed class SqlInventoryVersionStore(MultiChannelAgentDbContext db) : IInventoryVersionStore
{
    public async Task<IReadOnlyDictionary<Guid, long>> ReadAsync(
        IReadOnlyCollection<Guid> inventoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventoryIds);

        if (inventoryIds.Count == 0)
        {
            return new Dictionary<Guid, long>();
        }

        var ids = inventoryIds.Distinct().ToList();

        return await db.InventoryVersions
            .AsNoTracking()
            .Where(v => ids.Contains(v.InventoryId))
            .ToDictionaryAsync(v => v.InventoryId, v => v.Version, cancellationToken);
    }
}
```

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add immediately after the `IInventoryAuditRetentionStore` registration:

```csharp
        services.AddScoped<IInventoryVersionStore, SqlInventoryVersionStore>();
```

- [ ] **Step 6: Generate the migration and backfill existing Inventories**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddInventoryVersions \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure
```

Open the generated `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/*_AddInventoryVersions.cs` and confirm `Up` creates the `InventoryVersions` table with a primary key on `InventoryId` and - explicitly - **no** `AddForeignKey` call and no `ForeignKey` argument inside `CreateTable`. If EF generated one, the model has drifted from D5; fix the configuration rather than editing the migration. Then edit the file so `Up` ends with the backfill, immediately after the `CreateTable` call:

```csharp
            // Every Inventory that already exists gets its starting version here rather than lazily,
            // so the bump this migration enables is always an update of a row that exists - and so a
            // Participant watching an Inventory created before this deploy is told about its very
            // next change, not its second one. This backfill, and the save-time seeding of new
            // Inventories, are what keep the table consistent with Inventories in the absence of a
            // foreign key; the store's fallback insertion is the third line of defence, not the first.
            migrationBuilder.Sql(
                """
                INSERT INTO InventoryVersions (InventoryId, Version)
                SELECT i.Id, 0
                FROM Inventories AS i
                WHERE NOT EXISTS (SELECT 1 FROM InventoryVersions AS v WHERE v.InventoryId = i.Id);
                """);
```

`Down` needs no counterpart: dropping the table removes the backfilled rows with it.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~InventoryVersionBumpTests`
Expected: PASS, 10 tests.

- [ ] **Step 8: Prove nothing already shipped regressed**

Run: `dotnet test --configuration Release`
Expected: PASS. Every existing store test now exercises the save-time seam; a failure here means the seam changed behaviour it must not.

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure src/MultiChannelAgent.Application/Inventories/IInventoryVersionStore.cs \
        tests/MultiChannelAgent.IntegrationTests/InventoryVersionBumpTests.cs
git commit -m "feat: publish a per-Inventory version bump from the persistence seam"
```

---

## Task 8: The Participant-level Inventory invalidation stream

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/InventoryInvalidationReader.cs`
- Create: `src/MultiChannelAgent.Host/Endpoints/InventoryEventStreamResult.cs`
- Create: `src/MultiChannelAgent.Host/Endpoints/InventoryEventEndpoints.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryInventoryVersionStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryInvalidationReaderTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/InventoryEventStreamHttpTests.cs`

- [ ] **Step 1: Write the failing reader test**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryInventoryVersionStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>Minimal in-memory <see cref="IInventoryVersionStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryInventoryVersionStore : IInventoryVersionStore
{
    private readonly Dictionary<Guid, long> _versions = [];

    public void Set(Guid inventoryId, long version) => _versions[inventoryId] = version;

    public Task<IReadOnlyDictionary<Guid, long>> ReadAsync(
        IReadOnlyCollection<Guid> inventoryIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, long> result = _versions
            .Where(pair => inventoryIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return Task.FromResult(result);
    }
}
```

Create `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryInvalidationReaderTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// The whole payload of the Participant-level invalidation stream: which Inventories this Participant
/// may currently see, and what version each is at. Because the stream sends this complete picture on
/// every connection, every reconnect is a total resynchronization - which is why it needs no cursor
/// and can never miss a change that happened while a tab was closed.
/// </summary>
public class InventoryInvalidationReaderTests
{
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Owner = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly InMemoryInventoryStore _inventories = new();
    private readonly InMemoryInventoryVersionStore _versions = new();

    private InventoryInvalidationReader Reader => new(_inventories, _versions);

    [Fact]
    public async Task A_participant_with_no_memberships_is_told_about_nothing()
    {
        Assert.Empty(await Reader.ReadAsync(Participant, CancellationToken.None));
    }

    [Fact]
    public async Task Only_authorized_inventories_are_reported()
    {
        var mine = await CreateAsync("Mine", Participant);
        var theirs = await CreateAsync("Theirs", Owner);
        _versions.Set(mine, 7L);
        _versions.Set(theirs, 99L);

        var reported = await Reader.ReadAsync(Participant, CancellationToken.None);

        var only = Assert.Single(reported);
        Assert.Equal(mine, only.InventoryId);
        Assert.Equal(7L, only.Version);
    }

    [Fact]
    public async Task An_inventory_with_no_recorded_version_reports_as_never_changed()
    {
        var mine = await CreateAsync("Mine", Participant);

        var only = Assert.Single(await Reader.ReadAsync(Participant, CancellationToken.None));

        Assert.Equal(mine, only.InventoryId);
        Assert.Equal(0L, only.Version);
    }

    [Fact]
    public async Task The_report_is_ordered_stably_so_two_reads_of_the_same_state_are_identical()
    {
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync($"Warehouse {i}", Participant);
        }

        var first = await Reader.ReadAsync(Participant, CancellationToken.None);
        var second = await Reader.ReadAsync(Participant, CancellationToken.None);

        Assert.Equal(first.Select(v => v.InventoryId), second.Select(v => v.InventoryId));
        Assert.Equal(first.Select(v => v.InventoryId).Order(), first.Select(v => v.InventoryId));
    }

    private async Task<Guid> CreateAsync(string name, ParticipantId owner)
    {
        var inventory = Inventory.Create(name, owner, Guid.NewGuid().ToString(), DateTimeOffset.UnixEpoch);
        await _inventories.CreateAsync(
            inventory, Unit.CreateReservedEach(inventory.Id, DateTimeOffset.UnixEpoch), CancellationToken.None);
        return inventory.Id.Value;
    }
}
```

> `Inventory.Create` takes `(name, createdBy, clientRequestId, createdAt)` in that order, and
> `Unit.CreateReservedEach` takes `(inventoryId, createdAt)`. If either has moved, use whatever
> `InventoryCreationServiceTests` already uses to build an Inventory with its reserved Unit - do not
> invent a second construction path.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~InventoryInvalidationReaderTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'InventoryInvalidationReader' could not be found`.

- [ ] **Step 3: Write the reader**

Create `src/MultiChannelAgent.Application/Inventories/InventoryInvalidationReader.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>One authorized Inventory and the version its projections were last invalidated at.</summary>
public sealed record AuthorizedInventoryVersion(Guid InventoryId, long Version);

/// <summary>
/// The complete current invalidation picture for one Participant: every Inventory they may see right
/// now, with its current version.
///
/// This is deliberately a whole-state read rather than a change feed. Invalidation is idempotent -
/// a client needs to know what version each Inventory is at, never the history of how it got there -
/// so sending the complete picture makes reconnecting a resynchronization rather than a replay. That
/// is what lets the stream over this reader carry no cursor at all without losing anything (see the
/// stream's own documentation), and it removes three failure modes a cursor would have: a retention
/// sweep aging out unseen entries, an identity gap where a later change becomes visible before an
/// earlier one commits, and Membership granted or revoked while the client was disconnected.
/// Authorization is re-read every time for that last reason.
/// </summary>
public sealed class InventoryInvalidationReader(IInventoryStore inventoryStore, IInventoryVersionStore versionStore)
{
    public async Task<IReadOnlyList<AuthorizedInventoryVersion>> ReadAsync(
        ParticipantId participantId, CancellationToken cancellationToken)
    {
        var authorized = await inventoryStore.ListAuthorizedAsync(participantId, cancellationToken);
        if (authorized.Count == 0)
        {
            return [];
        }

        var ids = authorized.Select(record => record.InventoryId.Value).ToList();
        var versions = await versionStore.ReadAsync(ids, cancellationToken);

        return ids
            .Select(id => new AuthorizedInventoryVersion(id, versions.TryGetValue(id, out var version) ? version : 0L))
            .OrderBy(version => version.InventoryId)
            .ToList();
    }
}
```

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add immediately after the `InventoryListingService` registration:

```csharp
        services.AddScoped<InventoryInvalidationReader>();
```

- [ ] **Step 4: Run the reader test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~InventoryInvalidationReaderTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Write the failing HTTP test**

Create `tests/MultiChannelAgent.IntegrationTests/InventoryEventStreamHttpTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The Participant-level invalidation stream over real HTTP, backed by SQLite (fast, Docker-free).
/// The behaviour #35 asks for is "projections are invalidated after changes from any channel", so
/// these tests deliberately make the change through a different path than the one watching: a Turn
/// processed by the conversational worker, and a governance call made over HTTP.
/// </summary>
public sealed class InventoryEventStreamHttpTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Connecting_immediately_reports_the_current_version_of_every_authorized_inventory()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Watching Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Watched Warehouse");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenInventoryStreamAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var snapshot = Assert.Single(await reader.ReadAsync(1, timeout.Token));
        Assert.Equal("snapshot", snapshot.Name);

        // No issued identity, because this stream implements no cursor. See D5: what a client needs is
        // a function of current state, so a snapshot supersedes any resume point, and advertising an
        // `id` would promise semantics the server would silently ignore.
        Assert.Null(snapshot.Id);

        var reported = Assert.Single(JsonDocument.Parse(snapshot.Data).RootElement.GetProperty("inventories").EnumerateArray());
        Assert.Equal(inventoryId, reported.GetProperty("inventoryId").GetGuid());
        Assert.Equal(0L, reported.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_change_made_while_nothing_was_connected_is_in_the_next_connections_snapshot()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Reconnecting Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Reconnected Warehouse");

        using var timeout = new CancellationTokenSource(ReadTimeout);

        long firstSeenVersion;
        using (var firstConnection = await participant.OpenInventoryStreamAsync(timeout.Token))
        {
            await using var firstReader = await ServerSentEventReader.OpenAsync(firstConnection, timeout.Token);
            var snapshot = Assert.Single(await firstReader.ReadAsync(1, timeout.Token));
            firstSeenVersion = JsonDocument.Parse(snapshot.Data).RootElement
                .GetProperty("inventories").EnumerateArray().Single().GetProperty("version").GetInt64();
        }

        // Nothing is connected. This is precisely the window a cursor would exist to cover.
        await participant.SubmitAcceptedTurnAsync("native-invalidate-offline", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        using var secondConnection = await participant.OpenInventoryStreamAsync(timeout.Token);
        await using var secondReader = await ServerSentEventReader.OpenAsync(secondConnection, timeout.Token);
        var reconnected = Assert.Single(await secondReader.ReadAsync(1, timeout.Token));

        var reported = JsonDocument.Parse(reconnected.Data).RootElement
            .GetProperty("inventories").EnumerateArray().Single();

        // The change made while disconnected is not lost, and it did not need a Last-Event-ID to
        // survive: the snapshot IS the resume, because what the client needs is current state.
        Assert.Equal(inventoryId, reported.GetProperty("inventoryId").GetGuid());
        Assert.True(
            reported.GetProperty("version").GetInt64() > firstSeenVersion,
            "A reconnect must observe the change that happened while nothing was connected.");
        Assert.Null(reconnected.Id);
    }

    [Fact]
    public async Task A_change_made_through_the_conversation_invalidates_the_watching_tabs_projection()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Conversing Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Conversed Warehouse");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var reading = reader.ReadAsync(2, timeout.Token);

        await participant.SubmitAcceptedTurnAsync("native-invalidate-1", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        var events = await reading;

        Assert.Equal(["snapshot", "changed"], events.Select(e => e.Name));
        var changed = JsonDocument.Parse(events[1].Data).RootElement;
        Assert.Equal(inventoryId, changed.GetProperty("inventoryId").GetGuid());
        Assert.True(changed.GetProperty("version").GetInt64() > 0L);
    }

    [Fact]
    public async Task A_change_made_by_another_participant_over_http_invalidates_this_participants_projection()
    {
        var ownerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Granting Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Shared Warehouse");

        var editorHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var editor = await ConversationTestClient.SignInAsync(editorHttp, "Watching Editor");
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await editor.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var reading = reader.ReadAsync(2, timeout.Token);

        await owner.SubmitAcceptedTurnAsync("native-invalidate-2", "add stock Brass Rivets quantity 2");
        await ProcessUntilQuietAsync();

        var events = await reading;

        Assert.Equal(["snapshot", "changed"], events.Select(e => e.Name));
        Assert.Equal(inventoryId, JsonDocument.Parse(events[1].Data).RootElement.GetProperty("inventoryId").GetGuid());
    }

    [Fact]
    public async Task Losing_access_to_an_inventory_is_reported_so_the_projection_stops_being_shown()
    {
        var ownerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Revoking Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Revoked Warehouse");

        var editorHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var editor = await ConversationTestClient.SignInAsync(editorHttp, "Revoked Editor");
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await editor.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var reading = reader.ReadAsync(2, timeout.Token);

        var removal = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/inventories/{inventoryId}/members/{editor.ParticipantIdentifier}");
        var removalResponse = await owner.SendAsync(removal, withCsrf: true);
        Assert.True(removalResponse.IsSuccessStatusCode, $"Removing the member failed with {removalResponse.StatusCode}.");

        var events = await reading;

        Assert.Equal(["snapshot", "revoked"], events.Select(e => e.Name));
        Assert.Equal(inventoryId, JsonDocument.Parse(events[1].Data).RootElement.GetProperty("inventoryId").GetGuid());
    }

    [Fact]
    public async Task A_participant_only_ever_sees_their_own_inventories_on_this_stream()
    {
        var ownerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Private Owner");
        await owner.CreateAndSelectInventoryAsync("Nobody Elses Warehouse");

        var strangerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var stranger = await ConversationTestClient.SignInAsync(strangerHttp, "Unrelated Participant");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await stranger.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var snapshot = Assert.Single(await reader.ReadAsync(1, timeout.Token));
        Assert.Empty(JsonDocument.Parse(snapshot.Data).RootElement.GetProperty("inventories").EnumerateArray());
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
```

> If the member-removal route differs from `DELETE /api/inventories/{id}/members/{identifier}`, use
> whatever `InventoryGovernanceHttpTests` already calls. Do not add a second governance route.

- [ ] **Step 6: Run the HTTP test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~InventoryEventStreamHttpTests`
Expected: FAIL with `Assert.Equal() Failure: Expected: OK, Actual: NotFound` - `/api/inventory-events` is not mapped yet.

- [ ] **Step 7: Write the stream result**

Create `src/MultiChannelAgent.Host/Endpoints/InventoryEventStreamResult.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>One Inventory's identity and current version, as this stream reports it.</summary>
public sealed record InventoryVersionWire(Guid InventoryId, long Version);

/// <summary>The complete current picture, sent the moment a connection opens.</summary>
public sealed record InventorySnapshotWire(IReadOnlyList<InventoryVersionWire> Inventories);

/// <summary>An Inventory this Participant may no longer see, so its projection must stop being shown.</summary>
public sealed record InventoryRevokedWire(Guid InventoryId);

/// <summary>
/// One Participant's Inventory invalidation stream.
///
/// It opens with a complete snapshot of every Inventory the Participant may currently see and the
/// version each is at, then reports only differences for as long as the connection lasts.
///
/// It issues no event identities, and that is a claim about what the events carry rather than a
/// convenience. What a client needs from this stream is a function of current state - the version each
/// authorized Inventory is at right now - not of the event history. A `changed` event says nothing the
/// next snapshot does not say, and a `revoked` event says nothing the next snapshot's absence does not
/// say, so a missed event is a fact learned one snapshot later rather than a fact lost. A
/// `Last-Event-ID` could therefore not improve on reconnecting, while advertising one would promise
/// cursor semantics this handler does not implement and a client would be resuming from a position the
/// server ignores. `InventoryEventStreamHttpTests` proves the consequence directly: a change made
/// while nothing was connected is in the next connection's snapshot.
///
/// The authorized set is re-read on every pass, not just at the start, so a Membership granted or
/// revoked while the tab is open is reported rather than discovered on the next page load.
/// </summary>
public sealed class InventoryEventStreamResult(ParticipantId participantId) : IResult
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
        var options = httpContext.RequestServices.GetRequiredService<InventoryStreamOptions>();
        var cancellationToken = httpContext.RequestAborted;

        ServerSentEvents.PrepareResponse(httpContext.Response);

        var deadline = timeProvider.GetUtcNow() + options.MaxDuration;
        var lastWrite = timeProvider.GetUtcNow();

        try
        {
            var known = await ReadAsync(scopeFactory, cancellationToken);

            await ServerSentEvents.WriteEventAsync(
                httpContext.Response,
                id: null,
                "snapshot",
                JsonSerializer.Serialize(
                    new InventorySnapshotWire(known.Select(pair => new InventoryVersionWire(pair.Key, pair.Value)).ToList()),
                    SerializerOptions),
                cancellationToken);

            while (timeProvider.GetUtcNow() < deadline)
            {
                await Task.Delay(options.PollInterval, timeProvider, cancellationToken);

                var current = await ReadAsync(scopeFactory, cancellationToken);
                var wrote = false;

                foreach (var (inventoryId, version) in current)
                {
                    if (known.TryGetValue(inventoryId, out var seen) && seen == version)
                    {
                        continue;
                    }

                    await ServerSentEvents.WriteEventAsync(
                        httpContext.Response,
                        id: null,
                        "changed",
                        JsonSerializer.Serialize(new InventoryVersionWire(inventoryId, version), SerializerOptions),
                        cancellationToken);
                    wrote = true;
                }

                foreach (var inventoryId in known.Keys.Where(id => !current.ContainsKey(id)))
                {
                    await ServerSentEvents.WriteEventAsync(
                        httpContext.Response,
                        id: null,
                        "revoked",
                        JsonSerializer.Serialize(new InventoryRevokedWire(inventoryId), SerializerOptions),
                        cancellationToken);
                    wrote = true;
                }

                known = current;

                if (wrote)
                {
                    lastWrite = timeProvider.GetUtcNow();
                }
                else if (timeProvider.GetUtcNow() - lastWrite >= options.HeartbeatInterval)
                {
                    await ServerSentEvents.WriteHeartbeatAsync(httpContext.Response, cancellationToken);
                    lastWrite = timeProvider.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The tab closed or the connection dropped. This endpoint only ever reads, so there is
            // nothing to undo, and the next connection begins with a complete snapshot anyway.
        }
    }

    private async Task<Dictionary<Guid, long>> ReadAsync(IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<InventoryInvalidationReader>();

        return (await reader.ReadAsync(participantId, cancellationToken))
            .ToDictionary(version => version.InventoryId, version => version.Version);
    }
}
```

- [ ] **Step 8: Map the endpoint**

Create `src/MultiChannelAgent.Host/Endpoints/InventoryEventEndpoints.cs`:

```csharp
using System.Security.Claims;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the Participant-level Inventory invalidation stream. It is deliberately not under
/// <c>/api/inventories</c>: it is scoped to the Participant, not to one Inventory, and which
/// Inventories it reports is exactly what it re-derives on every pass.
/// </summary>
public static class InventoryEventEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // There is no ownership check to make and therefore no non-disclosing 404 to return: the
        // stream reports exactly the Inventories this Participant is authorized for and nothing else,
        // re-derived on every pass, so an unauthorized Inventory is not something it can be asked
        // about in the first place.
        endpoints.MapGet(
                "/api/inventory-events",
                (ClaimsPrincipal user) => (IResult)new InventoryEventStreamResult(user.GetParticipantId()))
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        return endpoints;
    }
}
```

In `src/MultiChannelAgent.Host/Program.cs`, add immediately after `app.MapInventoryRecoveryEndpoints();`:

```csharp
app.MapInventoryEventEndpoints();
```

- [ ] **Step 9: Run the HTTP tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~InventoryEventStreamHttpTests`
Expected: PASS, 6 tests.

- [ ] **Step 10: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/InventoryInvalidationReader.cs \
        src/MultiChannelAgent.Host/Endpoints/InventoryEventStreamResult.cs \
        src/MultiChannelAgent.Host/Endpoints/InventoryEventEndpoints.cs \
        src/MultiChannelAgent.Host/Program.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        tests/MultiChannelAgent.Application.Tests tests/MultiChannelAgent.IntegrationTests/InventoryEventStreamHttpTests.cs
git commit -m "feat: stream Participant-level Inventory invalidations from any channel"
```

---

## Task 9: Capture each Turn's Foundry conversation at acceptance

**Files:**
- Modify: `src/MultiChannelAgent.Application/Turns/IInboxStore.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs`
- Generate: `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/*_AddCapturedFoundryConversationBinding.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryInboxStore.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/TwoPartyGatedInboxStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TurnAcceptanceServiceTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlInboxStoreFifoTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/MultiChannelAgent.Application.Tests/TurnAcceptanceServiceTests.cs`, inside the existing class:

```csharp
    [Fact]
    public async Task An_accepted_turn_captures_the_foundry_conversation_it_was_accepted_under()
    {
        var inbox = new InMemoryInboxStore();
        var bindings = new InMemoryFoundryConversationBindingStore();
        var service = new TurnAcceptanceService(inbox, bindings);

        var accepted = await service.AcceptAsync(Request("native-capture-1"), Now, CancellationToken.None);

        var binding = Assert.Single(bindings.Bindings);
        var captured = await inbox.FindCapturedBindingAsync(accepted.TurnId, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(binding.FoundryConversationId, captured.FoundryConversationId);
        Assert.Equal(binding.Generation, captured.Generation);
    }

    [Fact]
    public async Task Work_accepted_before_a_reset_keeps_the_conversation_it_was_accepted_under()
    {
        var inbox = new InMemoryInboxStore();
        var bindings = new InMemoryFoundryConversationBindingStore();
        var service = new TurnAcceptanceService(inbox, bindings);

        var before = await service.AcceptAsync(Request("native-capture-2"), Now, CancellationToken.None);
        var rotated = bindings.Rotate(Participant, new ChannelConversationId(Conversation), Now.AddMinutes(1));
        var after = await service.AcceptAsync(Request("native-capture-3"), Now.AddMinutes(2), CancellationToken.None);

        var capturedBefore = await inbox.FindCapturedBindingAsync(before.TurnId, CancellationToken.None);
        var capturedAfter = await inbox.FindCapturedBindingAsync(after.TurnId, CancellationToken.None);

        Assert.NotNull(capturedBefore);
        Assert.NotNull(capturedAfter);

        // This is the whole reason the binding is captured rather than resolved at processing time:
        // work accepted before a reset can never end up in the history the reset created.
        Assert.NotEqual(capturedBefore.FoundryConversationId, capturedAfter.FoundryConversationId);
        Assert.Equal(rotated.FoundryConversationId, capturedAfter.FoundryConversationId);
        Assert.Equal(rotated.Generation, capturedAfter.Generation);
    }
```

Use whatever `Request(...)`, `Now`, `Participant`, and `Conversation` helpers/constants the existing file already defines; if it defines none, add:

```csharp
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private const string Conversation = "conversation-1";

    private static SubmitTurnRequest Request(string nativeMessageId) => new(
        nativeMessageId,
        Participant,
        Conversation,
        "web",
        ChannelPrincipal.EntraUser(Participant.Value.ToString(), "tenant-1"),
        ChannelCapabilities.Text | ChannelCapabilities.RichText | ChannelCapabilities.ProgressEvents,
        "hello",
        Locale: null,
        TraceId: null,
        WasInterrupted: false);
```

matching `SubmitTurnRequest`'s actual parameter order in `src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs`.

Add a rotation helper to `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryFoundryConversationBindingStore.cs`:

```csharp
    /// <summary>
    /// Starts a fresh Foundry conversation generation for this pair, exactly as the durable rotation
    /// does, so an Application-layer test can prove what a reset does to work either side of it
    /// without needing a database.
    /// </summary>
    public FoundryConversationBinding Rotate(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var key = (participantId, channelConversationId);
            var current = _bindings.TryGetValue(key, out var existing)
                ? existing
                : FoundryConversationBinding.CreateFirstGeneration(participantId, channelConversationId, now);

            var rotated = current with
            {
                FoundryConversationId = new FoundryConversationId(Guid.NewGuid()),
                Generation = current.Generation + 1,
                CreatedAt = now,
            };

            _bindings[key] = rotated;
            return rotated;
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnAcceptanceServiceTests`
Expected: FAIL to compile with `CS1729: 'TurnAcceptanceService' does not contain a constructor that takes 2 arguments` and `CS1061: 'InMemoryInboxStore' does not contain a definition for 'FindCapturedBindingAsync'`.

- [ ] **Step 3: Widen the inbox contract**

In `src/MultiChannelAgent.Application/Turns/IInboxStore.cs`, add above the interface:

```csharp
/// <summary>
/// The Foundry conversation identity a Turn was accepted under, read back for processing. Captured at
/// acceptance rather than resolved when the Turn is finally claimed, so a conversation reset between
/// those two moments can never move already-accepted work into the history the reset created.
/// </summary>
public sealed record CapturedConversationBinding(FoundryConversationId FoundryConversationId, int Generation);
```

Change `AcceptAsync` to take the binding, and add the read:

```csharp
    /// <summary>
    /// Atomically accepts <paramref name="turn"/> - stamped with the Foundry conversation generation
    /// <paramref name="binding"/> it was accepted under - unless a Turn for the same
    /// <see cref="InboundTurn.NativeMessageKey"/> is already durably accepted, including one accepted
    /// by a concurrent caller racing this same call, in which case that existing Turn is returned with
    /// <see cref="InboxAcceptResult.WasAlreadyAccepted"/> set, never a store-specific duplicate
    /// exception.
    /// </summary>
    Task<InboxAcceptResult> AcceptAsync(
        InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken);

    /// <summary>
    /// The Foundry conversation this Turn was accepted under, or null for a Turn accepted before that
    /// was captured. Never used for authorization - only to continue the right conversation.
    /// </summary>
    Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken);
```

- [ ] **Step 4: Resolve the binding at acceptance**

Replace `src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs`'s class declaration and body with:

```csharp
public sealed class TurnAcceptanceService(IInboxStore inboxStore, IFoundryConversationBindingStore bindingStore)
{
    public async Task<TurnAcceptanceResult> AcceptAsync(
        SubmitTurnRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = new NativeMessageKey(
            request.ParticipantId, new ChannelConversationId(request.ChannelConversationId), request.NativeMessageId);

        var existing = await inboxStore.FindByNativeMessageIdAsync(key, cancellationToken);
        if (existing is not null)
        {
            return new TurnAcceptanceResult(existing.TurnId, WasAlreadyAccepted: true);
        }

        // Every channel's text-only submission is the same shape: one content part, authored directly
        // by the authenticated Participant in this Turn.
        var turn = InboundTurn.Create(InboundTurnDraft.DirectText(
            request.NativeMessageId,
            request.ParticipantId,
            request.ChannelConversationId,
            request.Channel,
            request.Principal,
            request.Capabilities,
            request.ContentText,
            request.Locale,
            receivedAt,
            request.TraceId,
            request.WasInterrupted));

        // The conversation this Turn belongs to is decided here, at acceptance, and stamped on the
        // Turn itself. Resolving it later - when the Turn is finally claimed - would let a "New
        // conversation" in between silently move this already-accepted work into the fresh history,
        // which is exactly what a reset must not do.
        var binding = await bindingStore.GetOrCreateAsync(
            request.ParticipantId, new ChannelConversationId(request.ChannelConversationId), receivedAt, cancellationToken);

        var accepted = await inboxStore.AcceptAsync(turn, binding, cancellationToken);

        return new TurnAcceptanceResult(accepted.Turn.TurnId, accepted.WasAlreadyAccepted);
    }
}
```

Extend the class summary with:

```
/// Acceptance is also where a Turn's Foundry conversation generation is decided and stamped on it, so
/// a conversation reset can never retroactively move work that was already accepted.
```

- [ ] **Step 5: Use the captured binding when processing**

In `src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs`, change the factory to:

```csharp
public sealed class TurnExecutionContextFactory(
    IInboxStore inboxStore,
    IFoundryConversationBindingStore bindingStore,
    InventorySelectionService selectionService)
{
    public async Task<TurnExecutionContext> CreateAsync(InboundTurn turn, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // The conversation this Turn belongs to was decided when it was accepted, so a reset since
        // then leaves this Turn exactly where it was rather than dragging it into a fresh history.
        // The fallback covers only Turns accepted before that was captured; those predate any reset
        // by definition, so the current binding is the right one for them.
        var captured = await inboxStore.FindCapturedBindingAsync(turn.TurnId, cancellationToken)
            ?? await CurrentBindingAsync(turn, now, cancellationToken);

        var activeInventoryId = await selectionService.GetActiveInventoryIdAsync(
            turn.ParticipantId, turn.ChannelConversationId.Value, now, cancellationToken);

        return new TurnExecutionContext(
            turn.TurnId,
            turn.ParticipantId,
            turn.ChannelConversationId,
            captured.FoundryConversationId,
            captured.Generation,
            activeInventoryId,
            turn.TraceId,

            // Derived from the Turn's own direct content, here, before the model is asked anything -
            // so no proposal the model makes can ever be the reason a mutation was approved.
            DirectConfirmationEvidenceReader.Read(turn),
            turn.WasInterrupted);
    }

    private async Task<CapturedConversationBinding> CurrentBindingAsync(
        InboundTurn turn, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var binding = await bindingStore.GetOrCreateAsync(
            turn.ParticipantId, turn.ChannelConversationId, now, cancellationToken);

        return new CapturedConversationBinding(binding.FoundryConversationId, binding.Generation);
    }
}
```

Replace the second paragraph of its summary with:

```
/// Assembles the trusted <see cref="TurnExecutionContext"/> for one claimed Turn: reads back the
/// Foundry conversation generation the Turn was accepted under, and rechecks its Active Inventory
/// selection through <see cref="InventorySelectionService"/> - the same seam the web BFF uses - so
/// access lost since the selection was made is never trusted.
```

- [ ] **Step 6: Persist the captured binding**

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs`, add immediately after `WasInterrupted`:

```csharp
    /// <summary>
    /// The Foundry conversation this Turn was accepted into, captured at acceptance. Nullable only
    /// for Turns accepted before this was recorded; every acceptance since writes it, which is what
    /// stops a conversation reset from moving already-accepted work into the new history.
    /// </summary>
    public Guid? FoundryConversationId { get; set; }

    /// <summary>The generation of <see cref="FoundryConversationId"/> at the moment this Turn was accepted.</summary>
    public int? FoundryConversationGeneration { get; set; }
```

In `src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs`:

- Change the signature to `public async Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)`.
- Inside the `db.InboxEntries.Add(new InboxEntryEntity { ... })` initializer, add immediately after `WasInterrupted = turn.WasInterrupted,`:

```csharp
                FoundryConversationId = binding.FoundryConversationId.Value,
                FoundryConversationGeneration = binding.Generation,
```

- Add this method immediately after `FindByTurnIdAsync`:

```csharp
    public async Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var captured = await db.InboxEntries
            .AsNoTracking()
            .Where(e => e.TurnId == turnId.Value)
            .Select(e => new { e.FoundryConversationId, e.FoundryConversationGeneration })
            .FirstOrDefaultAsync(cancellationToken);

        return captured is { FoundryConversationId: { } conversationId, FoundryConversationGeneration: { } generation }
            ? new CapturedConversationBinding(new FoundryConversationId(conversationId), generation)
            : null;
    }
```

- [ ] **Step 7: Update the test doubles and every call site**

In `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryInboxStore.cs`, change `AcceptAsync` to take the binding, record it, add the read, and add one convenience overload:

```csharp
    private readonly Dictionary<Guid, CapturedConversationBinding> _capturedBindings = [];

    public Task<InboxAcceptResult> AcceptAsync(
        InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var existing = _turns.FirstOrDefault(t => t.NativeMessageKey == turn.NativeMessageKey);
            if (existing is not null)
            {
                return Task.FromResult(new InboxAcceptResult(existing, WasAlreadyAccepted: true));
            }

            _turns.Add(turn);
            _capturedBindings[turn.TurnId.Value] =
                new CapturedConversationBinding(binding.FoundryConversationId, binding.Generation);

            return Task.FromResult(new InboxAcceptResult(turn, WasAlreadyAccepted: false));
        }
    }

    /// <summary>
    /// Accepts a Turn into a first-generation binding for its own conversation. Not part of
    /// <see cref="IInboxStore"/> - it exists so the many tests that predate captured bindings, and
    /// genuinely do not care which generation a Turn was accepted under, keep saying what they mean
    /// instead of restating an irrelevant detail. Tests that DO care pass the binding explicitly.
    /// </summary>
    public Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, CancellationToken cancellationToken) =>
        AcceptAsync(
            turn,
            FoundryConversationBinding.CreateFirstGeneration(
                turn.ParticipantId, turn.ChannelConversationId, turn.ReceivedAt),
            cancellationToken);

    public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _capturedBindings.TryGetValue(turnId.Value, out var captured);
            return Task.FromResult(captured);
        }
    }
```

Apply the same three changes to `tests/MultiChannelAgent.Application.Tests/TestDoubles/TwoPartyGatedInboxStore.cs`, which wraps the same contract: widen its `AcceptAsync` to `(InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)` and forward the binding to `inner.AcceptAsync`; add the same convenience overload; and forward `FindCapturedBindingAsync` to `inner`.

Because both doubles keep a one-argument overload, **no Application-layer test that merely accepts a Turn has to change.** The remaining call sites are the ones that use the real store or the real factory, and they are exactly these:

| File | Change |
| --- | --- |
| `tests/MultiChannelAgent.Application.Tests/TurnAcceptanceServiceTests.cs` (6 sites: lines constructing `new TurnAcceptanceService(store)`, including the two `TwoPartyGatedInboxStore` ones) | `new TurnAcceptanceService(store, new InMemoryFoundryConversationBindingStore())`. The two gated services must share **one** binding store instance so the racing pair resolve the same binding, exactly as they share one inbox. |
| `tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs`, in `CreateFactory` | Add `var inboxStore = new InMemoryInboxStore();`, change the construction to `new TurnExecutionContextFactory(inboxStore, bindingStore, selectionService)`, and widen the returned tuple to `(TurnExecutionContextFactory Factory, InMemoryInventoryStore InventoryStore, InMemoryActiveInventorySelectionStore SelectionStore, InMemoryInboxStore Inbox, InMemoryFoundryConversationBindingStore Bindings)`. Then update its eight call sites: `var (factory, _, _) = CreateFactory();` becomes `var (factory, _, _, _, _) = CreateFactory();`, and the two `var (factory, inventoryStore, selectionStore) = CreateFactory();` become `var (factory, inventoryStore, selectionStore, _, _) = CreateFactory();`. |
| `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs`, in `CreateCoordinator` | `new TurnExecutionContextFactory(inbox, bindingStore, selectionService)` - the `inbox` local it already creates. |
| `tests/MultiChannelAgent.IntegrationTests/SqlInboxStoreFifoTests.cs` | Its private `AcceptAsync(SqlInboxStore, string, string, DateTimeOffset)` helper gains a trailing `FoundryConversationBinding? binding = null` parameter, defaulting inside the body to `FoundryConversationBinding.CreateFirstGeneration(SomeParticipant, new ChannelConversationId(conversationId), receivedAt)`, and passes it through. The two direct `storeA.AcceptAsync(` / `storeB.AcceptAsync(` calls in the concurrency test pass that same expression inline. |
| `tests/MultiChannelAgent.IntegrationTests/SqlInboxStoreConcurrencyTests.cs` (8 direct calls) | Each passes `FoundryConversationBinding.CreateFirstGeneration(<the turn's ParticipantId>, <the turn's ChannelConversationId>, <the turn's ReceivedAt>)`. Add a private static helper `Binding(InboundTurn turn)` returning exactly that, and pass `Binding(turnA)` and so on, so the expression appears once. |
| `tests/MultiChannelAgent.IntegrationTests/SqlDeliveryStoreClaimTests.cs` | One private `AcceptAsync` helper wraps `inbox.AcceptAsync`; give it the same `Binding(...)` treatment. |
| `tests/MultiChannelAgent.IntegrationTests/InboundTurnContractSqliteTests.cs` (3 direct calls) | Same `Binding(...)` helper, same substitution. |

`TurnAcceptanceService` and `TurnExecutionContextFactory` are already registered in DI, and `IFoundryConversationBindingStore` already is too, so no production registration changes. `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs` calls `acceptanceService.AcceptAsync(...)`, whose signature is unchanged.

Task 11 adds `AcceptedInSupersededConversation` to the context this factory builds; nothing here anticipates it.

- [ ] **Step 8: Generate the migration and backfill**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddCapturedFoundryConversationBinding \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure
```

Then edit the generated `*_AddCapturedFoundryConversationBinding.cs` so `Up` ends with the backfill, immediately after the two `AddColumn` calls:

```csharp
            // Turns accepted before this column existed predate any conversation reset by definition,
            // so the binding their conversation currently holds is the one they belong to. Leaving
            // them null would be correct too - the factory falls back to exactly this - but filling
            // them in keeps the fallback a genuinely dead path for anything the deploy left behind.
            migrationBuilder.Sql(
                """
                UPDATE i
                SET i.FoundryConversationId = b.FoundryConversationId,
                    i.FoundryConversationGeneration = b.Generation
                FROM InboxEntries AS i
                INNER JOIN FoundryConversationBindings AS b
                    ON b.ParticipantId = i.ParticipantId
                    AND b.ChannelConversationId = i.ChannelConversationId;
                """);
```

`Down` needs no counterpart: dropping the columns removes the backfilled values with them.

- [ ] **Step 9: Prove the durable capture with a relational test**

Append to `tests/MultiChannelAgent.IntegrationTests/SqlInboxStoreFifoTests.cs`:

```csharp
    [Fact]
    public async Task An_accepted_turn_reads_back_the_foundry_conversation_it_was_accepted_under()
    {
        using var db = CreateContext();
        var store = new SqlInboxStore(db);
        var binding = FoundryConversationBinding.CreateFirstGeneration(
            SomeParticipant, new ChannelConversationId("conversation-capture"), SameInstant);

        var turnId = await AcceptAsync(store, "native-capture", "conversation-capture", SameInstant, binding);

        var captured = await store.FindCapturedBindingAsync(turnId, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(binding.FoundryConversationId, captured.FoundryConversationId);
        Assert.Equal(1, captured.Generation);
    }
```

Change the file's existing private `AcceptAsync` helper to take an optional binding, defaulting to a
first generation for the conversation it is accepting into, and to pass it through to
`store.AcceptAsync`.

- [ ] **Step 10: Register nothing new, then run the whole suite**

`TurnAcceptanceService` and `TurnExecutionContextFactory` are already registered; their new dependencies resolve automatically.

Run: `dotnet test --configuration Release`
Expected: PASS. Every call site listed in Step 7 has been updated, so every `IInboxStore.AcceptAsync` now passes a binding.

- [ ] **Step 11: Commit**

```bash
git add src/MultiChannelAgent.Application src/MultiChannelAgent.Infrastructure tests
git commit -m "feat: capture each Turn's Foundry conversation generation at acceptance"
```

---

## Task 10: Atomic conversation rotation

**Files:**
- Modify: `src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs`
- Create: `src/MultiChannelAgent.Application/Turns/IConversationRotationStore.cs`
- Create: `src/MultiChannelAgent.Application/Turns/ConversationRotationService.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Turns/SqlConversationRotationStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryConversationRotationStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Turns/ConversationRotationServiceTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlConversationRotationStoreTests.cs`

- [ ] **Step 1: Write the failing relational test**

Create `tests/MultiChannelAgent.IntegrationTests/SqlConversationRotationStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage of what "New conversation" actually does to durable state: it starts a
/// fresh Foundry conversation generation and settles whatever confirmation was waiting, in one
/// transaction, and it touches neither Membership nor the Active Inventory selection. The last part
/// is the one an implementation could silently get wrong, so it is asserted directly rather than
/// inferred.
/// </summary>
public sealed class SqlConversationRotationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ChannelConversationId Conversation = new("web:profile-1");

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();

    public SqlConversationRotationStoreTests()
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
    public async Task Rotating_starts_a_new_generation_with_a_different_foundry_conversation()
    {
        using var db = CreateContext();
        var before = await new SqlFoundryConversationBindingStore(db)
            .GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        var result = await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(before.Generation + 1, result.Binding.Generation);
        Assert.NotEqual(before.FoundryConversationId, result.Binding.FoundryConversationId);
        Assert.False(result.ClearedPendingConfirmation);

        using var verifyDb = CreateContext();
        var row = await verifyDb.FoundryConversationBindings.AsNoTracking().SingleAsync();
        Assert.Equal(result.Binding.Generation, row.Generation);
        Assert.Equal(result.Binding.FoundryConversationId.Value, row.FoundryConversationId);
    }

    [Fact]
    public async Task Rotating_for_a_conversation_that_has_never_been_used_still_starts_a_fresh_generation()
    {
        using var db = CreateContext();

        var result = await Store(db).RotateAsync(Participant, Conversation, Now, CancellationToken.None);

        Assert.Equal(2, result.Binding.Generation);
        Assert.Single(await CreateContext().FoundryConversationBindings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Rotating_settles_the_one_pending_confirmation_as_a_conversation_reset()
    {
        using var db = CreateContext();
        var proposalStore = new SqlConfirmationProposalStore(db);
        var proposal = StockProposal();
        await proposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        var result = await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.True(result.ClearedPendingConfirmation);
        Assert.Equal(ProposalStatus.ConversationReset, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await proposalStore.FindPendingAsync(Participant, Conversation.Value, CancellationToken.None));
    }

    [Fact]
    public async Task Rotating_never_touches_membership_or_the_active_inventory_selection()
    {
        using var db = CreateContext();
        var selections = new SqlActiveInventorySelectionStore(db);
        await selections.UpsertAsync(
            new ActiveInventorySelection(Participant, Conversation.Value, new InventoryId(_inventoryId), Now),
            CancellationToken.None);

        await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        using var verifyDb = CreateContext();
        var selection = await new SqlActiveInventorySelectionStore(verifyDb)
            .FindAsync(Participant, Conversation.Value, CancellationToken.None);

        Assert.NotNull(selection);
        Assert.Equal(new InventoryId(_inventoryId), selection.InventoryId);
        Assert.Equal(1, await verifyDb.Memberships.AsNoTracking().CountAsync(m => m.ParticipantId == Participant.Value));
    }

    [Fact]
    public async Task Rotating_leaves_another_conversations_pending_confirmation_exactly_where_it_was()
    {
        using var db = CreateContext();
        var proposalStore = new SqlConfirmationProposalStore(db);
        var otherConversation = new ChannelConversationId("web:profile-2");
        var proposal = StockProposal(otherConversation.Value);
        await proposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(ProposalStatus.Pending, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Two_rotations_of_the_same_conversation_advance_two_distinct_generations()
    {
        using var seedDb = CreateContext();
        await new SqlFoundryConversationBindingStore(seedDb)
            .GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        using var firstDb = CreateContext();
        using var secondDb = CreateContext();

        // Two independent contexts rotating the same conversation. SQLite serializes writers, so this
        // proves the GUARD rather than a race: the second rotation must observe the first's generation
        // and advance past it instead of overwriting it. The genuinely concurrent case is Task 14's
        // SQL Server scenario, where two live HTTP resets are in flight at once.
        var results = await Task.WhenAll(
            Store(firstDb).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None),
            Store(secondDb).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None));

        // Each rotation is a real reset, so two of them advance two generations - never the same one
        // twice, and never a lost update where both write generation 2.
        Assert.Equal([2, 3], results.Select(r => r.Binding.Generation).Order());
        Assert.Equal(2, results.Select(r => r.Binding.FoundryConversationId).Distinct().Count());

        using var verifyDb = CreateContext();
        Assert.Equal(3, (await verifyDb.FoundryConversationBindings.AsNoTracking().SingleAsync()).Generation);
    }

    private static SqlConversationRotationStore Store(MultiChannelAgentDbContext db) =>
        new(db, new SqlFoundryConversationBindingStore(db));

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    private ConfirmationProposal StockProposal(string? conversationId = null)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            conversationId ?? Conversation.Value,
            new InventoryId(_inventoryId),
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", new UnitId(_unitId), "each",
                        LocationId: null, LocationName: null, Note: null,
                        Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = Participant.Value,
            DisplayName = "Resetting Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = Participant.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = _inventoryId,
            ParticipantId = Participant.Value,
            Role = MembershipRole.Owner,
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SqlConversationRotationStoreTests`
Expected: FAIL to compile with `CS0246: The type or namespace name 'SqlConversationRotationStore' could not be found` and `CS0117: 'ProposalStatus' does not contain a definition for 'ConversationReset'`.

- [ ] **Step 3: Add the terminal status**

In `src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs`, add to `ProposalStatus` immediately after `Interrupted`:

```csharp
    /// <summary>
    /// The Participant deliberately started a new conversation. Whatever was waiting for a "confirm"
    /// belonged to the conversation they just ended, so it stops being confirmable - while every
    /// authorization they hold, and the Inventory they were working in, are untouched.
    /// </summary>
    ConversationReset,
```

It is persisted by name into an existing `nvarchar(32)` column, so no migration is needed.

- [ ] **Step 4: Write the rotation seam**

Create `src/MultiChannelAgent.Application/Turns/IConversationRotationStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// What one conversation reset did: the fresh binding it established, and whether it had a pending
/// confirmation to settle.
/// </summary>
public sealed record ConversationRotationResult(FoundryConversationBinding Binding, bool ClearedPendingConfirmation);

/// <summary>
/// Starts a fresh Foundry conversation generation for one Participant's ChannelConversation and
/// settles whatever confirmation was waiting in it - as a single durable operation, because a reset
/// that rotated history without clearing the pending confirmation would leave a "confirm" the
/// Participant can still say pointing at work they have just walked away from.
///
/// What it must NOT touch is as much of the contract as what it must: Membership, the Active
/// Inventory selection, and every other authorization survive a reset untouched. Starting a new
/// conversation is not signing out.
/// </summary>
public interface IConversationRotationStore
{
    Task<ConversationRotationResult> RotateAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
```

Create `src/MultiChannelAgent.Infrastructure/Turns/SqlConversationRotationStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL-backed <see cref="IConversationRotationStore"/>. The generation increment is guarded by the
/// generation it was read at, so two resets racing the same conversation can never both write the
/// same next generation: the loser sees zero rows updated, re-reads, and rotates from where the
/// winner left it - the same bounded converge-on-the-winner shape
/// <see cref="SqlInboxStore.AcceptAsync"/> uses for its own guarded write. The pending confirmation
/// is settled in the same transaction, so no window exists in which history has rotated but a stale
/// "confirm" would still fire.
/// </summary>
public sealed class SqlConversationRotationStore(
    MultiChannelAgentDbContext db, IFoundryConversationBindingStore bindingStore) : IConversationRotationStore
{
    /// <summary>
    /// How many times a rotation may lose the guarded update to a concurrent reset before giving up.
    /// Bounded so a genuinely broken database can never spin here; each retry only ever loses to a
    /// real, committed competitor, so contention resolves immediately.
    /// </summary>
    private const int MaxRotateAttempts = 8;

    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);
    private static readonly string ResetStatus = nameof(ProposalStatus.ConversationReset);

    public async Task<ConversationRotationResult> RotateAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Establishes the binding when this conversation has never been used, so "New
            // conversation" is meaningful even as a Participant's very first action.
            var current = await bindingStore.GetOrCreateAsync(participantId, channelConversationId, now, cancellationToken);
            var nextConversationId = Guid.NewGuid();
            var nextGeneration = current.Generation + 1;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var rotated = await db.FoundryConversationBindings
                .Where(e => e.ParticipantId == participantId.Value
                    && e.ChannelConversationId == channelConversationId.Value
                    && e.Generation == current.Generation)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(e => e.FoundryConversationId, nextConversationId)
                        .SetProperty(e => e.Generation, nextGeneration)
                        .SetProperty(e => e.CreatedAt, now),
                    cancellationToken);

            if (rotated == 0)
            {
                // A concurrent reset advanced the generation between the read and this update. Roll
                // back and start over from whatever it left behind, so two resets advance two
                // generations rather than one silently overwriting the other.
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                if (attempt >= MaxRotateAttempts)
                {
                    throw new InvalidOperationException(
                        $"Could not rotate the conversation for Participant {participantId} after {MaxRotateAttempts} attempts.");
                }

                continue;
            }

            var cleared = await db.ConfirmationProposals
                .Where(p => p.ParticipantId == participantId.Value
                    && p.ChannelConversationId == channelConversationId.Value
                    && p.Status == PendingStatus)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.Status, ResetStatus)
                        .SetProperty(p => p.SettledAt, now)
                        .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
                    cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();

            return new ConversationRotationResult(
                current with
                {
                    FoundryConversationId = new FoundryConversationId(nextConversationId),
                    Generation = nextGeneration,
                    CreatedAt = now,
                },
                ClearedPendingConfirmation: cleared > 0);
        }
    }
}
```

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add immediately after the `IFoundryConversationBindingStore` registration:

```csharp
        services.AddScoped<IConversationRotationStore, SqlConversationRotationStore>();
        services.AddScoped<ConversationRotationService>();
```

- [ ] **Step 5: Run the relational test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SqlConversationRotationStoreTests`
Expected: PASS, 6 tests.

- [ ] **Step 6: Write the failing application-boundary test**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryConversationRotationStore.cs`:

```csharp
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IConversationRotationStore"/> for Application-layer unit tests. A
/// single lock makes reading the current generation and advancing it one indivisible step, mirroring
/// the real store's guarded update, so concurrent resets advance one generation each exactly like
/// production.
/// </summary>
public sealed class InMemoryConversationRotationStore(InMemoryFoundryConversationBindingStore bindings)
    : IConversationRotationStore
{
    private readonly object _gate = new();

    /// <summary>What the next rotation should report about a confirmation waiting in this conversation.</summary>
    public bool HasPendingConfirmation { get; set; }

    public Task<ConversationRotationResult> RotateAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var rotated = bindings.Rotate(participantId, channelConversationId, now);
            var cleared = HasPendingConfirmation;
            HasPendingConfirmation = false;

            return Task.FromResult(new ConversationRotationResult(rotated, cleared));
        }
    }
}
```

Create `tests/MultiChannelAgent.Application.Tests/Turns/ConversationRotationServiceTests.cs`:

```csharp
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Turns;

/// <summary>
/// The application boundary's view of "New conversation": what a channel is told happened, in a shape
/// it can render, with no store or persistence vocabulary leaking through.
/// </summary>
public class ConversationRotationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private const string Conversation = "web:profile-1";

    private readonly InMemoryFoundryConversationBindingStore _bindings = new();

    [Fact]
    public async Task Starting_a_new_conversation_reports_the_fresh_generation()
    {
        var store = new InMemoryConversationRotationStore(_bindings);
        var service = new ConversationRotationService(store);

        var view = await service.RotateAsync(Participant, Conversation, Now, CancellationToken.None);

        Assert.Equal(2, view.Generation);
        Assert.True(Guid.TryParse(view.FoundryConversationId, out _));
        Assert.False(view.ClearedPendingConfirmation);
    }

    [Fact]
    public async Task Starting_a_new_conversation_says_so_when_something_was_waiting_to_be_confirmed()
    {
        var store = new InMemoryConversationRotationStore(_bindings) { HasPendingConfirmation = true };
        var service = new ConversationRotationService(store);

        var view = await service.RotateAsync(Participant, Conversation, Now, CancellationToken.None);

        // Told, not silently discarded: a Participant who was mid-confirmation deserves to know their
        // proposal stopped being confirmable rather than discovering it by typing "confirm".
        Assert.True(view.ClearedPendingConfirmation);
    }

    [Fact]
    public async Task Each_new_conversation_advances_the_generation_again()
    {
        var store = new InMemoryConversationRotationStore(_bindings);
        var service = new ConversationRotationService(store);

        var first = await service.RotateAsync(Participant, Conversation, Now, CancellationToken.None);
        var second = await service.RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.NotEqual(first.FoundryConversationId, second.FoundryConversationId);
    }
}
```

- [ ] **Step 7: Write the service**

Create `src/MultiChannelAgent.Application/Turns/ConversationRotationService.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// What a channel is told a conversation reset did. <see cref="ClearedPendingConfirmation"/> is
/// there so a Participant who was mid-confirmation is told their proposal stopped being confirmable,
/// rather than discovering it by saying "confirm" into a conversation that no longer has one.
/// </summary>
public sealed record ConversationRotationView(
    string FoundryConversationId, int Generation, bool ClearedPendingConfirmation);

/// <summary>
/// Starts a fresh conversation for one Participant's ChannelConversation. This is the only entry
/// point channels use: the identities involved are always trusted context - the authenticated
/// Participant and their own channel conversation - never anything a request body claimed.
/// </summary>
public sealed class ConversationRotationService(IConversationRotationStore rotationStore)
{
    public async Task<ConversationRotationView> RotateAsync(
        ParticipantId participantId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await rotationStore.RotateAsync(
            participantId, new ChannelConversationId(channelConversationId), now, cancellationToken);

        return new ConversationRotationView(
            result.Binding.FoundryConversationId.ToString(),
            result.Binding.Generation,
            result.ClearedPendingConfirmation);
    }
}
```

- [ ] **Step 8: Run both test sets to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~ConversationRotationServiceTests`
Expected: PASS, 3 tests.

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SqlConversationRotationStoreTests`
Expected: PASS, 6 tests.

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs \
        src/MultiChannelAgent.Application/Turns/IConversationRotationStore.cs \
        src/MultiChannelAgent.Application/Turns/ConversationRotationService.cs \
        src/MultiChannelAgent.Infrastructure/Turns/SqlConversationRotationStore.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        tests/MultiChannelAgent.Application.Tests tests/MultiChannelAgent.IntegrationTests/SqlConversationRotationStoreTests.cs
git commit -m "feat: rotate a conversation's Foundry generation and pending confirmation atomically"
```

---

## Task 11: A Turn accepted in a superseded conversation leaves nothing confirmable

**Files:**
- Modify: `src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/IFoundryConversationBindingStore.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/ConfirmationProposalLifecycle.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs`
- Add: `src/MultiChannelAgent.Infrastructure/Persistence/FoundryConversationBindingSupersessionRead.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Turns/SqlFoundryConversationBindingStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalLifecycleTests.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryFoundryConversationBindingStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/FoundryConversationBindingSupersessionReadTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlFoundryConversationBindingSupersessionReadTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlSupersessionReadSerializationTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlUserDatabase.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/SqlIntegrationTestBase.cs`

This is D10. Task 10 settles what is pending **at the moment** a conversation is reset. This settles what a Turn accepted *before* that reset would otherwise leave pending **after** it - the one interleaving that would let a Participant confirm, in their new conversation, work they walked away from. Nothing here is persisted: the generation a Turn was accepted under is already durable on its inbox row from Task 9, and the proposal already names the Turn that proposed it, so this needs no column, no migration, and no backfill.

**The answer such a Turn records.** When the post-dispatch pass finds the conversation superseded and the decision in hand is `confirmation_required`, the coordinator records `ConfirmationProposalLifecycle.ConversationResetAnswer` instead - a `conflict` / `conversation_reset` answer with no payload and therefore no confirmation token. It is chosen before `ITurnResultStore.RecordAsync`, so the atomic terminal write is untouched and still records exactly one decision. See D10's "Why the answer is rewritten rather than recorded as decided". The rewrite is conditioned on the decision's category as well as the verdict, so a read-only Turn accepted before the reset keeps its own answer unchanged.

**Read D10's "The two orderings" before writing Step 7.** There are two distinct races, and only one of them is closed by the flag Steps 1-4 add:

- **Rotation before the Turn is claimed.** The factory computes `AcceptedInSupersededConversation: true`, and the pre-dispatch `ReconcileAsync` settles anything already pending before the model is asked anything.
- **Rotation while the Turn is being processed** - after the trusted context was assembled, and before or during the dispatch that stores a proposal. Here the flag is `false`, because it was true-at-the-time when it was computed. A settle that short-circuits on it does nothing, and the Turn leaves a confirmable proposal in the conversation the Participant just started.

So `SettleSupersededConversationAsync` **must not** gate on `context.AcceptedInSupersededConversation`. It re-reads the conversation's binding at settlement time and compares it with `context.FoundryConversationGeneration` - the generation the Turn was accepted under, which came off the Turn's own inbox row and therefore cannot go stale.

**And that re-read must be a locking read, not an ordinary one.** This is the third race, and it is invisible on any database without `READ_COMMITTED_SNAPSHOT` - which every Azure SQL database is created with, and which the container the SQL tests share does not have. `SqlConversationRotationStore.RotateAsync` bumps the generation and settles pending proposals inside one transaction; while it sits between its settle statement and its commit, an ordinary read is served from row versions and answers with the generation that rotation is *replacing*. Order that as **U < P < S < R** - the rotation's settle, then the Turn's proposal, then the re-read, then the commit - and neither mechanism fires: the rotation settled nothing because the proposal did not exist yet, and the re-read reported the conversation as current. So the re-read goes through its own seam, `IFoundryConversationBindingStore.ReadCurrentForSupersessionAsync`, whose contract is to serialize against an in-flight rotation of that binding row. `GetOrCreateAsync` keeps its cheap snapshot semantics: every other caller wants the current answer, not a queue behind a reset.

`ConfirmationProposalLifecycle` gains the binding store as a constructor dependency; it is already registered (`services.AddScoped<IFoundryConversationBindingStore, SqlFoundryConversationBindingStore>()`), and `ConfirmationProposalLifecycle` is itself already `AddScoped`, so **no production registration changes**. `ConfirmationProposalLifecycle.cs` already has `using MultiChannelAgent.Application.Turns;`, so no new using either.

The flag stays, and stays used, for the pre-dispatch `ReconcileAsync` only - there it is fresh, computed moments earlier in the same pass.

- [ ] **Step 1: Write the failing factory test**

Append to `tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs`, inside the existing class:

```csharp
    [Fact]
    public async Task A_turn_whose_conversation_has_since_been_reset_says_so()
    {
        var (factory, _, _, inbox, bindings) = CreateFactory();
        var turn = Turn("conversation-1");

        var acceptedUnder = await bindings.GetOrCreateAsync(
            SomeParticipant, turn.ChannelConversationId, Now, CancellationToken.None);
        await inbox.AcceptAsync(turn, acceptedUnder, CancellationToken.None);

        bindings.Rotate(SomeParticipant, turn.ChannelConversationId, Now.AddMinutes(1));

        var context = await factory.CreateAsync(turn, Now.AddMinutes(2), CancellationToken.None);

        Assert.True(context.AcceptedInSupersededConversation);

        // It still continues the history it was accepted into. A reset changes where NEW work goes;
        // it does not drag already-accepted work into the fresh conversation.
        Assert.Equal(acceptedUnder.FoundryConversationId, context.FoundryConversationId);
        Assert.Equal(acceptedUnder.Generation, context.FoundryConversationGeneration);
    }

    [Fact]
    public async Task A_turn_whose_conversation_is_still_the_current_one_is_not_marked_superseded()
    {
        var (factory, _, _, inbox, bindings) = CreateFactory();
        var turn = Turn("conversation-1");

        var acceptedUnder = await bindings.GetOrCreateAsync(
            SomeParticipant, turn.ChannelConversationId, Now, CancellationToken.None);
        await inbox.AcceptAsync(turn, acceptedUnder, CancellationToken.None);

        var context = await factory.CreateAsync(turn, Now.AddMinutes(1), CancellationToken.None);

        Assert.False(context.AcceptedInSupersededConversation);
    }

    // A reset in one ChannelConversation is not a reset in another: the binding is per pair, and a
    // Turn must never be treated as stale because some other conversation moved on.
    [Fact]
    public async Task A_reset_in_another_channel_conversation_never_marks_this_turn_superseded()
    {
        var (factory, _, _, inbox, bindings) = CreateFactory();
        var turn = Turn("conversation-1");
        var acceptedUnder = await bindings.GetOrCreateAsync(
            SomeParticipant, turn.ChannelConversationId, Now, CancellationToken.None);
        await inbox.AcceptAsync(turn, acceptedUnder, CancellationToken.None);
        bindings.Rotate(SomeParticipant, new ChannelConversationId("conversation-2"), Now.AddMinutes(1));

        var context = await factory.CreateAsync(turn, Now.AddMinutes(2), CancellationToken.None);

        Assert.False(context.AcceptedInSupersededConversation);
    }
```

and extend Task 9's existing `A_turn_accepted_before_bindings_were_captured_falls_back_to_the_current_one` with one more assertion rather than adding a near-duplicate test:

```csharp
        // Falling back to the current binding means it is current, so nothing about it is superseded.
        Assert.False(context.AcceptedInSupersededConversation);
```

That is only a statement about context-creation time: the post-dispatch settle in Step 7 compares generations rather than asking where the context's generation came from, so a reset landing later still settles what such a Turn stores.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnExecutionContextFactoryTests`
Expected: FAIL to compile with `CS1061: 'TurnExecutionContext' does not contain a definition for 'AcceptedInSupersededConversation'`.

- [ ] **Step 3: Detect supersession once, in the factory**

In `src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs`, add the flag as a trailing optional positional parameter - trailing so the two existing positional construction sites (`ReferenceToolDispatcherTests` and `ConfirmationProposalLifecycleTests`) keep compiling unchanged:

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
    bool WasInterrupted = false,

    /// <summary>
    /// True when this Turn was accepted under a Foundry conversation generation the conversation has
    /// since moved past - that is, the Participant started a new conversation after this Turn was
    /// accepted but before it was processed. The Turn still runs, and still runs against the history
    /// it was accepted into; what it must not do is leave a confirmation waiting in the conversation
    /// the Participant just started.
    /// </summary>
    bool AcceptedInSupersededConversation = false);
```

Then replace `TurnExecutionContextFactory.CreateAsync` in the same file with:

```csharp
    public async Task<TurnExecutionContext> CreateAsync(InboundTurn turn, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Two reads of the binding, answering two different questions. The CAPTURED one decides which
        // conversation this Turn belongs to - decided when it was accepted, so a reset since then
        // leaves it exactly where it was. The CURRENT one is read only to notice that a reset has
        // happened in between, and it is the ordinary get-or-create: this answer is a hint the
        // post-dispatch settle never relies on, so it has no reason to queue behind a reset. The
        // fallback covers Turns accepted before capture existed; those predate any reset by
        // definition, so the current binding is the right one for them and they are never superseded.
        var captured = await inboxStore.FindCapturedBindingAsync(turn.TurnId, cancellationToken);

        var current = await bindingStore.GetOrCreateAsync(
            turn.ParticipantId, turn.ChannelConversationId, now, cancellationToken);

        var accepted = captured ?? new CapturedConversationBinding(current.FoundryConversationId, current.Generation);

        var activeInventoryId = await selectionService.GetActiveInventoryIdAsync(
            turn.ParticipantId, turn.ChannelConversationId.Value, now, cancellationToken);

        return new TurnExecutionContext(
            turn.TurnId,
            turn.ParticipantId,
            turn.ChannelConversationId,

            // The captured conversation, deliberately: this Turn continues the history it was accepted
            // into, whatever the ChannelConversation has moved on to since.
            accepted.FoundryConversationId,
            accepted.Generation,
            activeInventoryId,
            turn.TraceId,

            // Derived from the Turn's own direct content, here, before the model is asked anything -
            // so no proposal the model makes can ever be the reason a mutation was approved.
            DirectConfirmationEvidenceReader.Read(turn),
            turn.WasInterrupted,

            // True as of this instant only. A reset can still land while this Turn is being processed,
            // which is why nothing that must hold at the END of the Turn may rely on this alone.
            accepted.Generation < current.Generation);
    }
```

and delete the now-unused `CurrentBindingAsync` helper Task 9 added.

- [ ] **Step 4: Run the factory tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnExecutionContextFactoryTests`
Expected: PASS, including the three new tests and the widened fallback test.

- [ ] **Step 5: Write the failing lifecycle tests**

First give the file one construction helper, because `ConfirmationProposalLifecycle` now takes two dependencies and five existing tests construct it with one. Add this next to the existing `Context` helper in `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalLifecycleTests.cs`:

```csharp
    /// <summary>
    /// The lifecycle under test, over a binding store whose conversation is on whatever generation
    /// the test put it on. Tests that are not about conversation resets get a fresh store, which
    /// establishes generation 1 on first read - exactly the generation their contexts carry.
    /// </summary>
    private static ConfirmationProposalLifecycle Lifecycle(
        InMemoryConfirmationProposalStore store, InMemoryFoundryConversationBindingStore? bindings = null) =>
        new(store, bindings ?? new InMemoryFoundryConversationBindingStore());
```

and add `using MultiChannelAgent.Application.Tests.TestDoubles;` to the file's usings (the binding double lives there, not under `TestDoubles.Inventories`).

Then replace all five existing `new ConfirmationProposalLifecycle(store)` expressions in the file with `Lifecycle(store)`. Nothing else about those tests changes: `ReconcileAsync` never reads the binding store at all - only the post-dispatch settle does - so a fresh empty one changes none of their behaviour.

Widen the file's existing `Context` helper with two more optional parameters, keeping every existing call unchanged:

```csharp
    private static TurnExecutionContext Context(
        InventoryId? activeInventoryId,
        bool wasInterrupted = false,
        string? conversation = null,
        int generation = 1,
        bool acceptedInSupersededConversation = false) => new(
        TurnId.NewId(),
        Participant,
        new ChannelConversationId(conversation ?? Conversation),
        new FoundryConversationId(Guid.NewGuid()),
        generation,
        activeInventoryId,
        TraceId: null,
        DirectConfirmationEvidence.None,
        wasInterrupted,
        acceptedInSupersededConversation);
```

`generation` is now load-bearing rather than incidental: it is the accepted generation `SettleSupersededConversationAsync` compares the re-read binding against.

**The Application-layer double has to be able to express an uncommitted rotation**, or no test here can tell a locking read from an ordinary one. Extend `tests/MultiChannelAgent.Application.Tests/TestDoubles/InMemoryFoundryConversationBindingStore.cs` with an in-flight rotation the test controls, a read that parks behind it, and a way to observe that parking:

```csharp
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource? _rotationInFlight;
    private FoundryConversationBinding? _uncommittedRotation;

    /// <summary>How many times the supersession seam - and not an ordinary read - was asked.</summary>
    public int SupersessionReadCount { get; private set; }

    /// <summary>
    /// Completes once a supersession read has parked behind the in-flight rotation, bounded so a
    /// caller that reads the committed generation instead fails the test rather than hanging the run.
    /// </summary>
    public Task SupersessionReadParkedAsync() => _parked.Task.WaitAsync(ParkTimeout);

    /// <summary>
    /// Answers only once no rotation is mid-flight for this pair, exactly as a read that takes the
    /// binding row's lock does: while a reset holds the row uncommitted this parks, and it resumes
    /// with whatever that reset left committed. An ordinary <see cref="GetOrCreateAsync"/> answers
    /// straight from the committed value throughout, which is how a test can tell the two apart.
    /// </summary>
    public async Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        Task? inFlight;
        lock (_gate)
        {
            SupersessionReadCount++;
            inFlight = _rotationInFlight?.Task;
        }

        if (inFlight is not null)
        {
            _parked.TrySetResult();
            await inFlight;
        }

        lock (_gate)
        {
            return _bindings.TryGetValue((participantId, channelConversationId), out var binding) ? binding : null;
        }
    }

    /// <summary>
    /// Starts a rotation that has written this pair's binding but has not committed - the state a
    /// real rotation transaction is in between its generation bump and its commit. Every ordinary
    /// read still sees the old generation until <see cref="CommitRotation"/>.
    /// </summary>
    public void BeginRotation(ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _uncommittedRotation = Rotated(participantId, channelConversationId, now);
            _rotationInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Commits the in-flight rotation, publishing its generation and releasing anything parked behind it.</summary>
    public void CommitRotation()
    {
        TaskCompletionSource? inFlight;
        lock (_gate)
        {
            if (_uncommittedRotation is not { } rotated || _rotationInFlight is null)
            {
                throw new InvalidOperationException("No rotation is in flight to commit.");
            }

            _bindings[(rotated.ParticipantId, rotated.ChannelConversationId)] = rotated;
            inFlight = _rotationInFlight;
            _rotationInFlight = null;
            _uncommittedRotation = null;
        }

        inFlight.TrySetResult();
    }
```

Factor the existing `Rotate` helper's body into a private `Rotated(participantId, channelConversationId, now)` that computes the next binding without storing it, so `Rotate` (commits immediately) and `BeginRotation` (does not) share one definition of what a rotation produces.

Now append these tests to `ConfirmationProposalLifecycleTests`:

```csharp
    [Fact]
    public async Task A_Turn_accepted_in_a_conversation_that_has_since_been_reset_invalidates_the_pending_proposal()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await Lifecycle(store).ReconcileAsync(
            Context(SomeInventory, acceptedInSupersededConversation: true), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.ConversationReset, settled);
        Assert.Equal(ProposalStatus.ConversationReset, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    /// <summary>
    /// Puts one pending proposal in a conversation whose binding has since rotated to a new
    /// generation, exactly as a deliberate "New conversation" leaves it - except that here nothing
    /// settled the proposal, which is the very hole the post-dispatch pass exists to close.
    /// </summary>
    private static async Task<(InMemoryConfirmationProposalStore Proposals, InMemoryFoundryConversationBindingStore Bindings, ConfirmationProposal Proposal)>
        RotatedSinceAcceptanceAsync()
    {
        var (proposals, proposal) = await PendingAsync();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);
        bindings.Rotate(Participant, new ChannelConversationId(Conversation), Now);

        return (proposals, bindings, proposal);
    }

    // The single property the whole post-dispatch settlement exists for. The context was assembled
    // BEFORE the reset, so the flag it captured truthfully said "current" at the time - and by the
    // time the Turn's proposal was written, it no longer was.
    [Fact]
    public async Task A_conversation_rotated_after_the_context_was_assembled_still_settles_what_the_Turn_just_proposed()
    {
        var (proposals, bindings, proposal) = await RotatedSinceAcceptanceAsync();

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory, acceptedInSupersededConversation: false), Now, CancellationToken.None);

        Assert.True(settlement.ConversationWasSuperseded);
        Assert.Equal(ProposalStatus.ConversationReset, settlement.Settled);
        Assert.Equal(ProposalStatus.ConversationReset, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_conversation_still_on_the_generation_the_Turn_was_accepted_under_leaves_its_proposal_confirmable()
    {
        var (proposals, proposal) = await PendingAsync();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.False(settlement.ConversationWasSuperseded);
        Assert.Null(settlement.Settled);
        Assert.Equal(ProposalStatus.Pending, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    // A Turn that proposes nothing leaves nothing to settle - but the conversation it ran in has
    // still been left behind, and only the caller can decide what that means for its own answer.
    [Fact]
    public async Task A_reset_conversation_with_nothing_pending_still_reports_the_conversation_as_left_behind()
    {
        var proposals = new InMemoryConfirmationProposalStore();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);
        bindings.Rotate(Participant, new ChannelConversationId(Conversation), Now);

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.True(settlement.ConversationWasSuperseded);
        Assert.Null(settlement.Settled);
    }

    [Fact]
    public async Task Settling_a_superseded_conversation_leaves_another_conversations_proposal_alone()
    {
        var (proposals, bindings, mine) = await RotatedSinceAcceptanceAsync();
        var other = Proposal("conversation-2");
        await proposals.StoreAsync(other, Now, CancellationToken.None);

        await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.ConversationReset, await proposals.FindStatusAsync(mine.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await proposals.FindStatusAsync(other.Id, CancellationToken.None));
    }

    // The settlement asks a different question from every other reader of this binding - "is a reset
    // committing right now?" - and it has to be asked through the seam that can answer it. Reading
    // through the ordinary get-or-create would create a binding no one asked for, which is how this
    // test tells the two apart without needing a database.
    [Fact]
    public async Task Settling_never_creates_a_binding_for_a_conversation_that_has_none()
    {
        var proposals = new InMemoryConfirmationProposalStore();
        var bindings = new InMemoryFoundryConversationBindingStore();

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.False(settlement.ConversationWasSuperseded);
        Assert.Empty(bindings.Bindings);
        Assert.Equal(1, bindings.SupersessionReadCount);
    }

    // The whole reason the seam exists. While a reset holds the binding row uncommitted, the
    // generation every ordinary read still sees is the one being replaced; answering from it would
    // report the conversation as current and leave this Turn's proposal confirmable in a conversation
    // that is about to be gone.
    [Fact]
    public async Task Settling_waits_for_a_reset_that_is_still_committing_rather_than_reading_the_generation_it_replaces()
    {
        var (proposals, proposal) = await PendingAsync();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);
        bindings.BeginRotation(Participant, new ChannelConversationId(Conversation), Now);

        // The stale answer this must not give: an ordinary read is served the committed generation
        // throughout, and the Turn was accepted under exactly that.
        var stale = await bindings.GetOrCreateAsync(
            Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);
        Assert.Equal(1, stale.Generation);

        var settling = Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        await bindings.SupersessionReadParkedAsync();
        bindings.CommitRotation();

        var settlement = await settling;
        Assert.True(settlement.ConversationWasSuperseded);
        Assert.Equal(ProposalStatus.ConversationReset, settlement.Settled);
        Assert.Equal(ProposalStatus.ConversationReset, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    /// <summary>
    /// A binding store whose read fails, so a test can prove the settlement reports the fault rather
    /// than quietly treating a conversation it could not read as still current.
    /// </summary>
    private sealed class FailingBindingStore(Exception failure) : IFoundryConversationBindingStore
    {
        public Task<FoundryConversationBinding> GetOrCreateAsync(
            ParticipantId participantId,
            ChannelConversationId channelConversationId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromException<FoundryConversationBinding>(failure);

        public Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
            ParticipantId participantId,
            ChannelConversationId channelConversationId,
            CancellationToken cancellationToken) =>
            Task.FromException<FoundryConversationBinding?>(failure);
    }

    [Fact]
    public async Task Settling_a_superseded_conversation_propagates_cancellation()
    {
        var (proposals, _) = await PendingAsync();
        var lifecycle = new ConfirmationProposalLifecycle(
            proposals, new FailingBindingStore(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.SettleSupersededConversationAsync(Context(SomeInventory), Now, CancellationToken.None));
    }

    // Failing to read the binding is not evidence that the conversation is still current. Swallowing
    // it would answer "nothing to settle" and leave a confirmable proposal behind; letting it out
    // leaves the whole Turn to be retried, which is the only safe reading of "we do not know".
    [Fact]
    public async Task A_binding_read_that_fails_is_never_swallowed_into_leaving_the_proposal_confirmable()
    {
        var (proposals, proposal) = await PendingAsync();
        var lifecycle = new ConfirmationProposalLifecycle(
            proposals, new FailingBindingStore(new InvalidOperationException("binding read failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.SettleSupersededConversationAsync(Context(SomeInventory), Now, CancellationToken.None));

        Assert.Equal(ProposalStatus.Pending, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
```

`InMemoryFoundryConversationBindingStore.Rotate` is the helper Task 9 Step 1 added; it is already in place by the time this task runs.

- [ ] **Step 6: Run the lifecycle tests to verify they fail**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~ConfirmationProposalLifecycleTests`
Expected: FAIL to compile with `CS1729: 'ConfirmationProposalLifecycle' does not contain a constructor that takes 2 arguments`, `CS1061: 'ConfirmationProposalLifecycle' does not contain a definition for 'SettleSupersededConversationAsync'`, and `CS0535: 'FailingBindingStore' does not implement interface member 'IFoundryConversationBindingStore.ReadCurrentForSupersessionAsync'`.

- [ ] **Step 7: Give the settlement a seam that serializes, and teach the lifecycle to use it**

**7a. Widen the binding store contract.** In `src/MultiChannelAgent.Application/Turns/IFoundryConversationBindingStore.cs`, add a second read beside `GetOrCreateAsync`. It is a separate method rather than a change to the existing one on purpose: every other caller wants the cheapest current answer, and none of them should start queueing behind a reset.

```csharp
    /// <summary>
    /// Reads the binding this pair currently holds for the one question
    /// <see cref="Inventories.ConfirmationProposalLifecycle.SettleSupersededConversationAsync"/> asks:
    /// has this conversation moved past the generation a Turn was accepted under? That question is
    /// decided against a reset that may be committing right now, so this read carries a stronger
    /// contract than <see cref="GetOrCreateAsync"/> and exists separately rather than changing it.
    ///
    /// <b>It must serialize against an in-flight rotation of this pair's binding.</b> A caller that
    /// returns while a rotation holds the row uncommitted has answered from a generation that is
    /// already being replaced; an implementation must instead wait for that writer to finish and
    /// answer from what it left behind. Ordinary reads have no such obligation, and must not grow one
    /// - every other caller wants the cheapest current answer, not a queue behind a reset.
    ///
    /// <b>It must never create a binding.</b> Returning null means this pair has none at all, which
    /// means no rotation has ever run for it and nothing can have been superseded. A caller treats
    /// that exactly as "still current" - it is the same answer a first-generation binding would give,
    /// without writing a row that only a read asked for.
    /// </summary>
    Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken);
```

**7b. Write the statement once per provider.** Add `src/MultiChannelAgent.Infrastructure/Persistence/FoundryConversationBindingSupersessionRead.cs`, following `InventoryVersionPublication` exactly - one constant per provider, identities as parameters, chosen by `database.IsSqlServer()`:

```csharp
    /// <summary>
    /// <c>UPDLOCK</c> makes this read take an update lock on the binding row instead of reading a
    /// version of it, and update locks are held to the end of the transaction. That is exactly the
    /// mutual exclusion the check needs: this read and
    /// <c>SqlConversationRotationStore</c>'s generation bump both want conflicting locks on the same
    /// row, so they are strictly ordered rather than passing through each other. Whichever goes
    /// second sees what the first did.
    ///
    /// <c>HOLDLOCK</c> is deliberately not added. It would additionally range-lock the key when no row
    /// is there, and no caller needs that: the binding for a (Participant, ChannelConversation) is
    /// created when a Turn is accepted, long before that Turn can be processed, so this read never
    /// decides about a row that is still to appear.
    /// </summary>
    private const string SqlServerRead =
        """
        SELECT ParticipantId, ChannelConversationId, FoundryConversationId, Generation, CreatedAt
        FROM FoundryConversationBindings WITH (UPDLOCK)
        WHERE ParticipantId = {0} AND ChannelConversationId = {1}
        """;

    /// <summary>
    /// SQLite has no table hints and needs none: it admits one writer at a time, so a reader inside a
    /// transaction cannot pass through a rotation that is still writing. This is the provider the
    /// fast, Docker-free tests run on; SQL Server is the production one.
    /// </summary>
    private const string SqliteRead =
        """
        SELECT ParticipantId, ChannelConversationId, FoundryConversationId, Generation, CreatedAt
        FROM FoundryConversationBindings
        WHERE ParticipantId = {0} AND ChannelConversationId = {1}
        """;

    public static FormattableString Statement(
        DatabaseFacade database, Guid participantId, string channelConversationId)
    {
        ArgumentNullException.ThrowIfNull(database);

        return FormattableStringFactory.Create(
            database.IsSqlServer() ? SqlServerRead : SqliteRead, participantId, channelConversationId);
    }
```

Neither statement ends in a semicolon, so EF may compose over it without the text becoming invalid.

**7c. Implement it in the SQL store.** In `src/MultiChannelAgent.Infrastructure/Turns/SqlFoundryConversationBindingStore.cs`, add the locking read in its own short transaction, joining the caller's when there is one, and abandoning safely on any fault:

```csharp
    public async Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await LockingReadAsync(participantId, channelConversationId, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var binding = await LockingReadAsync(participantId, channelConversationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return binding;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    private async Task<FoundryConversationBinding?> LockingReadAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        // No tracking: this is a read that decides something, never the start of a write, and a
        // tracked copy would linger in a scope shared by a whole batch of Turns.
        var rows = await db.FoundryConversationBindings
            .FromSql(FoundryConversationBindingSupersessionRead.Statement(
                db.Database, participantId.Value, channelConversationId.Value))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : Project(rows[0]);
    }
```

Factor the existing `FindAsync` projection into a private `static FoundryConversationBinding Project(FoundryConversationBindingEntity entity)` so both reads map the row one way, and add `using MultiChannelAgent.Infrastructure.Persistence.Entities;`.

**7d. Take the store as a dependency and add the settlement.** In `src/MultiChannelAgent.Application/Inventories/ConfirmationProposalLifecycle.cs`:

```csharp
public sealed class ConfirmationProposalLifecycle(
    IConfirmationProposalStore proposalStore, IFoundryConversationBindingStore bindingStore)
```

Add the answer shape the settlement reports, above the class - two facts, not one, because a reset that committed before the Turn's proposal was stored settles that proposal itself and leaves this pass nothing to settle:

```csharp
/// <summary>
/// What the post-dispatch pass found: whether the conversation this Turn belongs to had been left
/// behind by the time its work was done, and the status of anything it settled there. They are
/// separate answers on purpose. A reset that committed before this Turn's proposal was stored settles
/// it in its own transaction, so this pass finds nothing left to settle - and the Turn must still not
/// answer with a confirmation, because the proposal it would offer has already stopped being one.
/// </summary>
public sealed record SupersededConversationSettlement(bool ConversationWasSuperseded, ProposalStatus? Settled)
{
    public static readonly SupersededConversationSettlement StillCurrent = new(false, null);
}
```

Add the answer such a Turn records, and the code it carries, as members of the lifecycle - it is the one place that owns "this stopped being confirmable because the conversation was reset":

```csharp
    /// <summary>The machine code an answer carries when a reset is why nothing is confirmable any more.</summary>
    public const string ConversationResetCode = "conversation_reset";

    private const string ConversationResetSummary =
        "That was proposed in a conversation you have since left, so there is nothing to confirm here.";

    /// <summary>
    /// What a Turn is answered with when the work it proposed stopped being confirmable before it
    /// could ever be offered. It deliberately carries no proposal payload and therefore no
    /// confirmation token: handing back a token that can never be redeemed would be worse than saying
    /// nothing. The category is a conflict with current state, not a failure - the Turn was processed
    /// exactly as asked, and the conversation it was asked in simply moved on.
    /// </summary>
    public static readonly ModelDecision ConversationResetAnswer = new()
    {
        Category = OutcomeCategory.Conflict,
        Code = ConversationResetCode,
        Summary = ConversationResetSummary,
        Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, ConversationResetSummary)],
    };
```

(this needs `using MultiChannelAgent.Domain.Turns;` for `OutcomeCategory`).

Add the new case as the **first** arm of the existing `switch` in `ReconcileAsync`, so it is evaluated before the interruption and access checks:

```csharp
        var status = context switch
        {
            // The strongest statement of the four: this Turn belongs to a conversation the Participant
            // has already left, so nothing waiting in it is still an open question. The flag is
            // trusted HERE and only here: this runs immediately after the context was assembled, so it
            // is as fresh as a read can be.
            { AcceptedInSupersededConversation: true } => ProposalStatus.ConversationReset,

            // A cut-off utterance is not a statement of intent, and a conversation that has just been
            // interrupted is not one in which a stored approval should keep waiting to be triggered.
            { WasInterrupted: true } => ProposalStatus.Interrupted,
```

(the remaining arms are unchanged), and add this method after `ReconcileAsync`:

```csharp
    /// <summary>
    /// Run after this Turn's work is done and before its Outcome is recorded: re-reads the binding and
    /// settles whatever is pending in this conversation when the generation the Turn was accepted
    /// under has been left behind. It deliberately ignores
    /// <see cref="TurnExecutionContext.AcceptedInSupersededConversation"/> - that flag answered the
    /// question at context assembly, and the whole point of this second pass is the window that opens
    /// after it.
    ///
    /// Why this closes that window. Write P for the instant this Turn's proposal became durable, S
    /// for the binding read below, and U and R for the rotation's own settle statement and its
    /// commit, with U before R. P always precedes S, because the proposal is stored during dispatch
    /// and this runs after dispatch.
    ///
    /// The read below serializes against an in-flight rotation of this conversation's binding
    /// (<see cref="IFoundryConversationBindingStore.ReadCurrentForSupersessionAsync"/>), so it and the
    /// rotation's own generation bump are strictly ordered - which leaves exactly two cases.
    ///
    /// <list type="bullet">
    /// <item><b>The read goes first.</b> It answers with the generation the Turn was accepted under,
    /// this pass settles nothing - and the rotation, whose bump and whose settle both run after that
    /// read has finished, therefore runs its settle after P. It finds the proposal durable and
    /// Pending and settles it inside its own transaction.</item>
    /// <item><b>The rotation goes first.</b> The read waits for it and answers with the newer
    /// generation, so this pass settles whatever the Turn left pending.</item>
    /// </list>
    ///
    /// There is no third case, and that is the whole point of reading through that seam rather than
    /// the ordinary one: an unserialized read can answer from a generation a rotation is in the middle
    /// of replacing, and then neither side settles anything - the rotation's settle ran before P, and
    /// this pass believed the conversation was current.
    /// </summary>
    public async Task<SupersededConversationSettlement> SettleSupersededConversationAsync(
        TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A read that fails is not evidence the conversation is still current, so it is left to
        // propagate: the Turn records no Outcome and is retried, rather than completing on a guess.
        var current = await bindingStore.ReadCurrentForSupersessionAsync(
            context.ParticipantId, context.ChannelConversationId, cancellationToken);

        // No binding at all means no rotation has ever run for this conversation, so nothing this Turn
        // did can be stranded in one. Creating a binding to say so is not this reader's business.
        if (current is null || context.FoundryConversationGeneration >= current.Generation)
        {
            return SupersededConversationSettlement.StillCurrent;
        }

        // One set-based settle rather than a read followed by a write: nothing here needs to know
        // which proposal it is, and anything Pending in a conversation that has been left behind is
        // by definition no longer confirmable.
        var settled = await proposalStore.InvalidatePendingAsync(
            context.ParticipantId,
            context.ChannelConversationId.Value,
            ProposalStatus.ConversationReset,
            now,
            cancellationToken);

        return new SupersededConversationSettlement(
            ConversationWasSuperseded: true, settled > 0 ? ProposalStatus.ConversationReset : null);
    }
```

The comparison is `>=`, not `==`: anything at or below the current generation is not superseded, and a Turn can never legitimately carry a generation *above* it.

- [ ] **Step 8: Run the lifecycle tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~ConfirmationProposalLifecycleTests`
Expected: PASS - fourteen tests, the five this file already had plus the nine Step 5 added.

- [ ] **Step 9: Fix the coordinator harness so a mutation-capable Turn can actually reach a proposal**

`TurnProcessingCoordinatorTests`' existing `CreateCoordinator` builds the dispatch services over stores it then throws away, and hands `ConfirmationProposalLifecycle` a **second, separate** `InMemoryConfirmationProposalStore` from the one the dispatcher writes to. Nothing noticed, because no test here ever reached tool dispatch. Every test in this step does, so the harness has to expose the stores it builds and share one proposal store.

In `tests/MultiChannelAgent.Application.Tests/TurnProcessingCoordinatorTests.cs`, introduce a fixture record and a builder beside the existing `CreateCoordinator`, and reduce `CreateCoordinator` to a thin wrapper over it so the twelve tests that destructure its tuple are untouched:

```csharp
    /// <summary>
    /// Everything one coordinator is wired over, so a test that is about what processing does to
    /// Inventory state can seed and read the very stores the dispatch runs against.
    /// </summary>
    private sealed record CoordinatorHarness(
        TurnProcessingCoordinator Coordinator,
        InMemoryInboxStore Inbox,
        InMemoryOutcomeStore Outcomes,
        InMemoryDeliveryStore Deliveries,
        InMemoryTurnResultStore ResultStore,
        InMemoryFoundryConversationBindingStore Bindings,
        InMemoryConfirmationProposalStore Proposals,
        InMemoryInventoryStore Inventories,
        InMemoryActiveInventorySelectionStore Selections,
        InMemoryStockStore Stock,
        InMemoryInventoryReferenceStore References);
```

Move `CreateCoordinator`'s body into `CreateHarness(...)` with the same parameters, returning that record; inside it, declare `proposalStore` **once** and pass the same instance to `InventorySelectionService`, `InMemoryStockChangeSetStore`, `StockChangeSetService`, `InventoryConfirmationService` and `new ConfirmationProposalLifecycle(proposalStore, bindingStore)`. `CreateCoordinator` then becomes:

```csharp
    {
        var harness = CreateHarness(timeProvider, modelBoundary, progressStore, progressEventStore);

        return (harness.Coordinator, harness.Inbox, harness.Outcomes, harness.Deliveries, harness.ResultStore, harness.Bindings);
    }
```

That also deletes the helper's old comment beginning *"These tests never exercise the tool-dispatch path"*, which stops being true here.

**Seed exactly what a real confirmation needs**, or every assertion below is vacuous - a Turn with no Active Inventory answers `no_active_inventory`, one with no Membership answers `forbidden`, one with no matching row answers `not_found`, and a non-empty row answers `forget_requires_zero_quantity`. None of those leave a proposal, so "nothing is pending" would pass without confirmation ever having been possible:

```csharp
    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private const string MutationConversation = "conversation-1";

    private static async Task SeedForgettableStockAsync(CoordinatorHarness harness)
    {
        harness.Inventories.GrantMembership(SomeInventory, SomeParticipant, MembershipRole.Editor, Now);
        harness.References.AddUnit(SomeInventory, EachUnit, "each");
        harness.Stock.CreateRow(SomeInventory, "Steel Bolts", EachUnit, "each", null, null, null, Quantity.Zero);
        await harness.Selections.UpsertAsync(
            new ActiveInventorySelection(SomeParticipant, MutationConversation, SomeInventory, Now),
            CancellationToken.None);
    }

    private static InboundTurn ForgetTurn(string nativeMessageId, DateTimeOffset receivedAt) =>
        TestTurns.Text(nativeMessageId, SomeParticipant, MutationConversation, "forget stock Steel Bolts", null, receivedAt, null);

    /// <summary>
    /// Starts a new conversation the way the durable rotation does it: the generation advances and
    /// whatever was pending in the conversation being left behind stops being confirmable, as one
    /// step. <see cref="IConversationRotationStore"/> owns that atomicity in production; this states
    /// the same two effects so a coordinator test can reset a conversation without a database.
    /// </summary>
    private static async Task ResetConversationAsync(CoordinatorHarness harness, DateTimeOffset now)
    {
        harness.Bindings.Rotate(SomeParticipant, new ChannelConversationId(MutationConversation), now);
        await harness.Proposals.InvalidatePendingAsync(
            SomeParticipant, MutationConversation, ProposalStatus.ConversationReset, now, CancellationToken.None);
    }

    private static async Task<ConfirmationProposal?> PendingProposalAsync(CoordinatorHarness harness) =>
        await harness.Proposals.FindPendingAsync(SomeParticipant, MutationConversation, CancellationToken.None);
```

Add the gate the race test needs, next to the existing `CountingModelBoundary` and `CapturingModelBoundary`:

```csharp
    /// <summary>
    /// Parks inside the model call - after the trusted context is assembled and before anything is
    /// dispatched - so a test can drive the exact window in which a conversation rotates while the
    /// Turn holds no proposal yet. The handoffs are completed sources rather than sleeps, so the race
    /// is deterministic; <see cref="ReachedAsync"/> bounds the wait only so a regression that never
    /// reaches the model call fails the test instead of hanging the run.
    /// </summary>
    private sealed class GatedModelBoundary(IModelBoundary inner) : IModelBoundary
    {
        private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the Turn is parked inside the model call.</summary>
        public Task ReachedAsync() => _reached.Task.WaitAsync(ReachTimeout);

        public void Release() => _released.TrySetResult();

        public async Task<ModelProposal> ProposeAsync(
            InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
        {
            _reached.TrySetResult();
            await _released.Task;

            return await inner.ProposeAsync(turn, context, cancellationToken);
        }
    }
```

**One test needs a proposal to become durable and a reset to land in the same instant**, which no gate on the model boundary can express. Give `InMemoryConfirmationProposalStore` a hook that runs once a proposal is stored, and a way to see every proposal it has ever held:

```csharp
    /// <summary>
    /// Every proposal this store has held, settled or not. Test-only: it lets a test prove a proposal
    /// really was created and say where it ended up, which "nothing is pending" alone cannot.
    /// </summary>
    public IReadOnlyCollection<ConfirmationProposal> Proposals => _rows.Values.Select(row => row.Proposal).ToList();

    /// <summary>
    /// Runs once a proposal is durable, before <see cref="StoreAsync"/> returns. Test-only: it places
    /// another durable event - a conversation reset, say - at the exact instant between a proposal
    /// becoming confirmable and whatever its Turn goes on to do, without threads or timing.
    /// </summary>
    public Func<Task>? AfterStore { get; set; }
```

`StoreAsync` becomes `async`, and awaits `AfterStore` after `_rows[proposal.Id] = new Row(proposal, ProposalStatus.Pending, null);` and before it returns.

Then append these four tests to `TurnProcessingCoordinatorTests`:

```csharp
    // The property the whole post-dispatch settlement exists for: a mutation-capable Turn accepted
    // under one Foundry conversation generation must never leave a confirmable proposal behind once
    // the Participant has started a new conversation.
    [Fact]
    public async Task A_mutation_accepted_before_a_reset_leaves_nothing_confirmable_in_the_new_conversation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var harness = CreateHarness(timeProvider);
        await SeedForgettableStockAsync(harness);

        // First, prove the path is genuinely reachable: an identical Turn with no reset anywhere near
        // it really does reach confirmation and really does leave something confirmable behind.
        var beforeAnyReset = ForgetTurn("native-1", Now);
        await AcceptAsync(harness.Inbox, harness.Bindings, beforeAnyReset);
        await harness.Coordinator.ProcessPendingAsync(CancellationToken.None);

        var control = await harness.Outcomes.FindAsync(beforeAnyReset.TurnId, CancellationToken.None);
        Assert.Equal(OutcomeCategory.ConfirmationRequired, control!.Category);
        Assert.NotNull(await PendingProposalAsync(harness));

        // A second Turn is accepted into the same conversation - still the generation it was queued
        // under - and only then does the Participant start a new conversation.
        var acceptedBeforeTheReset = ForgetTurn("native-2", Now.AddSeconds(1));
        await AcceptAsync(harness.Inbox, harness.Bindings, acceptedBeforeTheReset);
        await ResetConversationAsync(harness, Now.AddSeconds(2));
        Assert.Null(await PendingProposalAsync(harness));

        await harness.Coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Null(await PendingProposalAsync(harness));
        var outcome = await harness.Outcomes.FindAsync(acceptedBeforeTheReset.TurnId, CancellationToken.None);
        Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category);
        Assert.Equal(ConfirmationProposalLifecycle.ConversationResetCode, outcome.Code);
    }

    // The window the captured flag alone cannot close: at context assembly the conversation really
    // was current, and the reset lands while the Turn is inside the model call - before it has any
    // proposal for the rotation to settle. Only re-reading the binding after dispatch catches it.
    [Fact]
    public async Task A_reset_that_lands_while_a_turn_is_being_processed_still_settles_what_it_goes_on_to_propose()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var gate = new GatedModelBoundary(new ScriptedModelBoundary());
        var harness = CreateHarness(timeProvider, gate);
        await SeedForgettableStockAsync(harness);
        var turn = ForgetTurn("native-1", Now);
        await AcceptAsync(harness.Inbox, harness.Bindings, turn);

        var processing = harness.Coordinator.ProcessPendingAsync(CancellationToken.None);
        try
        {
            await gate.ReachedAsync();

            // The hole itself: the trusted context has already been assembled and found the
            // conversation current, and there is nothing for a reset to settle yet.
            Assert.Null(await PendingProposalAsync(harness));
            await ResetConversationAsync(harness, Now.AddSeconds(1));
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(1, await processing);

        var created = Assert.Single(harness.Proposals.Proposals);
        Assert.Equal(turn.TurnId, created.ProposedInTurnId);
        Assert.Equal(
            ProposalStatus.ConversationReset,
            await harness.Proposals.FindStatusAsync(created.Id, CancellationToken.None));
        Assert.Null(await PendingProposalAsync(harness));
        var outcome = await harness.Outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category);
    }

    // A reset accepted before this Turn had proposed anything is only one of the two orders. In the
    // other, this Turn's proposal is already durable when the reset runs, so the reset settles it -
    // and there is nothing left for the post-dispatch pass to settle. The answer must still not offer
    // a confirmation: a token that has already stopped working is worse than no token at all.
    [Fact]
    public async Task A_reset_that_settles_the_proposal_itself_still_never_answers_with_a_confirmation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var harness = CreateHarness(timeProvider);
        await SeedForgettableStockAsync(harness);
        var turn = ForgetTurn("native-1", Now);
        await AcceptAsync(harness.Inbox, harness.Bindings, turn);

        harness.Proposals.AfterStore = () => ResetConversationAsync(harness, Now.AddSeconds(1));

        Assert.Equal(1, await harness.Coordinator.ProcessPendingAsync(CancellationToken.None));

        var created = Assert.Single(harness.Proposals.Proposals);
        Assert.Equal(
            ProposalStatus.ConversationReset,
            await harness.Proposals.FindStatusAsync(created.Id, CancellationToken.None));
        Assert.Null(await PendingProposalAsync(harness));

        var outcome = await harness.Outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category);
        Assert.Equal(ConfirmationProposalLifecycle.ConversationResetCode, outcome.Code);
        Assert.Null(outcome.Payload);
        Assert.All(
            harness.Deliveries.Deliveries,
            delivery => Assert.DoesNotContain("token", delivery.Payload, StringComparison.OrdinalIgnoreCase));
    }

    // A reset ends what can still be confirmed, not what has already been asked. A read accepted
    // before it proposes nothing, so it simply answers - from the conversation it was accepted under.
    [Fact]
    public async Task A_read_accepted_before_a_reset_still_answers_from_the_conversation_it_was_accepted_under()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var capturing = new CapturingModelBoundary(new ScriptedModelBoundary());
        var harness = CreateHarness(timeProvider, capturing);
        await SeedForgettableStockAsync(harness);
        var turn = TestTurns.Text("native-1", SomeParticipant, MutationConversation, "list stock including zero", null, Now, null);
        var acceptedUnder = await harness.Bindings.GetOrCreateAsync(
            SomeParticipant, turn.ChannelConversationId, Now, CancellationToken.None);
        await harness.Inbox.AcceptAsync(turn, acceptedUnder, CancellationToken.None);

        await ResetConversationAsync(harness, Now.AddSeconds(1));
        Assert.Equal(1, await harness.Coordinator.ProcessPendingAsync(CancellationToken.None));

        var outcome = await harness.Outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.Equal(OutcomeStatus.Completed, outcome!.Status);
        Assert.Equal(OutcomeCategory.Completed, outcome.Category);
        Assert.Contains("Steel Bolts", outcome.Payload);
        Assert.Equal(acceptedUnder.FoundryConversationId, Assert.Single(capturing.Invocations).Foundry);
        Assert.Empty(harness.Proposals.Proposals);
    }
```

No new `using` directives are needed: the file already imports `MultiChannelAgent.Application.Inventories` (the services, `StockToolDispatcher` and `ConfirmationProposalLifecycle`), `MultiChannelAgent.Application.Tests.TestDoubles` and `...TestDoubles.Inventories` (every double used above), `MultiChannelAgent.Domain.Inventories` (`InventoryId`, `UnitId`, `MembershipRole`, `ActiveInventorySelection`, `Quantity`, `ProposalStatus`), and `MultiChannelAgent.Domain.Turns` (`ChannelConversationId`, `OutcomeCategory`, `OutcomeStatus`).

- [ ] **Step 10: Run the coordinator tests and check each one fails where it should**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnProcessingCoordinatorTests`
Expected: FAIL - but not uniformly, and *where* each one fails is the thing to read. The four tests Step 9 added drive different orderings, so they have different red signals, and one of them is a control that must already be green. Every other test in the file must still pass; if any of them broke, the `CreateCoordinator` replacement was applied wrongly.

| Test | Expected before Step 11 |
| --- | --- |
| `A_mutation_accepted_before_a_reset_leaves_nothing_confirmable_in_the_new_conversation` | **FAIL at the second `Assert.Null(await PendingProposalAsync(harness))`** - the one after the second `ProcessPendingAsync`, because that Turn stored a proposal into the rotated conversation and nothing settled it. The two assertions before it must **pass**: `Assert.Equal(OutcomeCategory.ConfirmationRequired, control!.Category)` is the seeded control that stops the failure being vacuous, and the first `Assert.Null`, taken right after `ResetConversationAsync`, is the reset doing its own job. |
| `A_reset_that_lands_while_a_turn_is_being_processed_still_settles_what_it_goes_on_to_propose` | **FAIL at `Assert.Equal(ProposalStatus.ConversationReset, await harness.Proposals.FindStatusAsync(created.Id, ...))`**, which reports `Pending`. The `Assert.Null` inside the `try` passes on purpose - it *states the hole*: while the Turn is parked at the model boundary there is nothing for the reset to settle. Everything up to and including `Assert.Single(harness.Proposals.Proposals)` passes too; the proposal really is created, it is simply left confirmable. |
| `A_reset_that_settles_the_proposal_itself_still_never_answers_with_a_confirmation` | **FAIL at `Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category)`**. This ordering's settlement is done by the reset itself, so the proposal assertions and `Assert.Null` all pass - what is wrong is the *answer*, which still offers a code for a proposal that has already stopped being confirmable. It fails on the truthfulness of the Outcome, not on any leftover pending state. |
| `A_read_accepted_before_a_reset_still_answers_from_the_conversation_it_was_accepted_under` | **PASS.** It is a control, not a red driver: a read leaves no proposal, so there is nothing for Step 11 to change about it. It is here to fail *later* - if the settlement ever starts reshaping an answer that had nothing to do with confirmation, or if the factory stops processing a Turn in the conversation it was accepted under. |

Do not require all four to fail, and do not require any of them to fail on the same line: only the first has a pending proposal to be surprised by, and a change that made the second or third fail at `Assert.Null` instead would mean the fixture, not the behaviour, is what moved.

**Check the seeded fixture first if the mutation test fails early.** `A_mutation_accepted_before_a_reset_leaves_nothing_confirmable_in_the_new_conversation` asserts `OutcomeCategory.ConfirmationRequired` on its control Turn *before* anything about settlement, precisely so a seeding mistake cannot masquerade as the behaviour under test. If it fails on that line, the fixture is wrong and the `Assert.Null` below it would have passed for the wrong reason. The failure message names the category the Turn actually reached:

| Actual category | What is missing |
| --- | --- |
| `Invalid` (code `no_active_inventory`) | The `ActiveInventorySelection` upsert, or it names a different ChannelConversation than the Turn does |
| `Forbidden` | The `GrantMembership` call, or it grants a role below `MembershipRole.Editor` |
| `NotFound` | The `CreateRow` for `Steel Bolts`, or its name does not normalize to what `forget stock Steel Bolts` asks for |
| `Conflict` (code `forget_requires_zero_quantity`) | The seeded row has a non-zero quantity |
| `Completed` (summary starting `Echoed:`) | The content text is not one `ScriptedModelBoundary` recognizes as `forget stock <reference>` |

Fix the fixture until that control assertion passes and the mutation test fails on its `Assert.Null`, and only then implement.

- [ ] **Step 11: Settle it in the coordinator, and answer truthfully**

In `src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs`, inside `ProcessOneAsync`, insert immediately after the `var decision = ...` assignment and before `var outcome = Outcome.Record(...)`:

```csharp
        // The reconcile above ran before this Turn had proposed anything, so it could not settle a
        // proposal that did not exist yet. A reset that landed while this Turn was in the model call
        // or in dispatch would therefore find nothing, and this Turn would go on to leave a
        // confirmable proposal in a conversation the Participant has already left. Re-reading the
        // binding here - never trusting the flag the context captured earlier - is what closes that
        // window, and it happens before the Outcome is recorded so the answer never offers a token
        // that has already stopped working. Whether this pass or the reset itself settled the
        // proposal makes no difference to the answer: what decides it is that the conversation moved
        // on at all.
        var settlement = await proposalLifecycle.SettleSupersededConversationAsync(
            executionContext, now, cancellationToken);

        if (settlement.ConversationWasSuperseded && decision.Category == OutcomeCategory.ConfirmationRequired)
        {
            decision = ConfirmationProposalLifecycle.ConversationResetAnswer;
        }
```

`decision` therefore has to be declared with `var` and reassigned, which it already is. The condition has two halves and both are load-bearing: gating on `settlement.Settled` instead of `ConversationWasSuperseded` would hand back a live-looking token whenever the reset settled the proposal itself, and dropping the category check would let this reshape a read's perfectly good answer.

Extend the class summary's last paragraph with:

```
/// It also reconciles pending confirmation state against the freshly assembled trusted context before
/// the Turn is interpreted, so an interrupted Turn, a switched Active Inventory, or lost access can
/// never leave a confirmable proposal behind - and once more after dispatch, so neither can a
/// conversation reset that landed while this Turn was being processed.
```

No constructor change: `ConfirmationProposalLifecycle` is already injected, and it is the lifecycle - not the coordinator - that gained the binding-store dependency.

- [ ] **Step 12: Run the coordinator tests, then the whole Application suite**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests --filter FullyQualifiedName~TurnProcessingCoordinatorTests`
Expected: PASS, including the new tests.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests`
Expected: PASS. The harness now shares one proposal store, which is what the pre-existing tests always assumed.

- [ ] **Step 13: Prove the locking read against real databases**

Three tests, in `tests/MultiChannelAgent.IntegrationTests`, each covering something the layer above it cannot.

**13a. The statement's shape, per provider, with no database at all.** Add `FoundryConversationBindingSupersessionReadTests.cs`. Build one `MultiChannelAgentDbContext` on `UseSqlServer` and one on `UseSqlite` (neither ever connects), then assert `FoundryConversationBindingSupersessionRead.Statement(db.Database, ...)`:

- on SQL Server its `Format` contains `WITH (UPDLOCK)`;
- on SQLite its `Format` contains no `UPDLOCK`;
- on both, `Format` contains neither identity as text, and `GetArguments()` is exactly `[participantId, channelConversationId]`.

This is where a regression to an ordinary read shows up in a second, without Docker.

**13b. What the read answers, on SQLite.** Add `SqlFoundryConversationBindingSupersessionReadTests.cs`, following `SqlConversationRotationStoreTests`' shared-cache in-memory SQLite fixture. Seed a `ParticipantEntity` first - the binding row has a foreign key to it. Assert it reads back the current generation; reads back generation 2 after a real `SqlConversationRotationStore.RotateAsync`; returns null **and writes no row** for a conversation that has none; reads only the pair it was asked about; leaves `db.Database.CurrentTransaction` null behind it; and works inside a transaction the caller already holds.

**13c. The U < P < S < R window, on real SQL Server with RCSI.** Add `SqlSupersessionReadSerializationTests.cs`, deriving `SqlIntegrationTestBase`, as a `[SkippableFact]` guarded by `Skip.IfNot(DockerAvailable, ...)`.

It cannot use the container's shared database: `MsSqlBuilder`'s connection string names `master`, and `READ_COMMITTED_SNAPSHOT` cannot be set there at all. So add `SqlUserDatabase.cs` - an `IAsyncDisposable` that creates a uniquely named database from `master`, exposes its connection string, enables and reports the setting, and drops it again after clearing the pool - and expose the container's own string from the base class as `protected string? ServerConnectionString`. Nothing else changes: every other SQL scenario keeps sharing `master`, which is what makes them fast.

The scenario then:

1. creates its database, enables `READ_COMMITTED_SNAPSHOT` **before anything connects**, and asserts it is on - a run without it would prove nothing;
2. applies migrations to it, seeds a Participant/Inventory/Membership/reserved Unit, and establishes generation 1;
3. starts the real `SqlConversationRotationStore.RotateAsync` on a context carrying a `DbCommandInterceptor` that pauses in `NonQueryExecutedAsync` on `[ConfirmationProposals]` - after the generation bump and the settle, before the commit. That is **U**;
4. stores a proposal from a second context. That is **P**, and it is the reason the reset settled nothing;
5. asserts, as the control, that an ordinary `GetOrCreateAsync` still reads generation 1 - the stale answer the defect believed;
6. starts `SettleSupersededConversationAsync` on a third context, asserts it does not complete within a short window, and asserts a request on that database is genuinely lock-blocked (`sys.dm_exec_requests`), so a check that had merely not started yet cannot pass this;
7. releases the gate in a `finally`, then asserts the rotation reached generation 2 with `ClearedPendingConfirmation` false - the hole, stated - and that the settlement reports `ConversationWasSuperseded` with `Settled` of `ConversationReset`, and that the stored proposal's status is `ConversationReset` with nothing pending.

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SupersessionRead`
Expected: PASS. 13c skips wherever Docker is unavailable; CI sets `REQUIRE_DOCKER_TESTS=true`, which removes its ability to skip.

- [ ] **Step 14: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs \
        src/MultiChannelAgent.Application/Turns/IFoundryConversationBindingStore.cs \
        src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs \
        src/MultiChannelAgent.Application/Inventories/ConfirmationProposalLifecycle.cs \
        src/MultiChannelAgent.Infrastructure/Persistence/FoundryConversationBindingSupersessionRead.cs \
        src/MultiChannelAgent.Infrastructure/Turns/SqlFoundryConversationBindingStore.cs \
        tests/MultiChannelAgent.Application.Tests \
        tests/MultiChannelAgent.IntegrationTests
git commit -m "fix: stop a Turn from a reset conversation leaving a confirmable proposal"
```

---

## Task 12: The New conversation endpoint

**Files:**
- Create: `src/MultiChannelAgent.Host/Endpoints/ConversationEndpoints.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/ConversationRotationHttpTests.cs`
- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/ConversationRotationHttpTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// "New conversation" over real HTTP, backed by SQLite (fast, Docker-free). The criterion is
/// deliberately two-sided: it must rotate model history and clear pending confirmation state, and it
/// must not remove a single thing the Participant is authorized to do - which includes the Inventory
/// they had selected, their Memberships, and a file import they had waiting in the browser.
/// </summary>
public sealed class ConversationRotationHttpTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Starting_a_new_conversation_rotates_the_foundry_conversation_generation()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Resetting Participant");
        await participant.CreateAndSelectInventoryAsync("Reset Warehouse");
        await participant.SubmitAcceptedTurnAsync("native-reset-1", "list stock");
        await ProcessUntilQuietAsync();

        var before = await SingleBindingAsync();

        var response = await participant.StartNewConversationAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(before.Generation + 1, body.GetProperty("generation").GetInt32());
        Assert.NotEqual(before.FoundryConversationId.ToString(), body.GetProperty("foundryConversationId").GetString());

        var after = await SingleBindingAsync();
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.NotEqual(before.FoundryConversationId, after.FoundryConversationId);
    }

    [Fact]
    public async Task Starting_a_new_conversation_keeps_every_authorization_and_the_active_inventory()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Authorized Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Kept Warehouse");

        Assert.Equal(HttpStatusCode.OK, (await participant.StartNewConversationAsync()).StatusCode);

        var bootstrap = (await participant.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(inventoryId.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());
        Assert.Single(bootstrap.GetProperty("inventories").EnumerateArray());
    }

    [Fact]
    public async Task Starting_a_new_conversation_clears_the_confirmation_that_was_waiting()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Confirming Participant");
        await participant.CreateAndSelectInventoryAsync("Pending Warehouse");

        await participant.SubmitAcceptedTurnAsync("native-reset-2", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        // A batch, because a change set of more than one change always asks for confirmation whatever
        // its changes are, so this needs no particular prior Stock state. A single
        // `forget stock Steel Bolts` would instead be refused with forget_requires_zero_quantity -
        // Forget can never stand in for Remove - and would leave nothing pending to clear.
        var proposalTurn = await participant.SubmitAcceptedTurnAsync(
            "native-reset-3", "change stock: add Steel Bolts quantity 1; add Brass Rivets quantity 2");
        await ProcessUntilQuietAsync();

        var proposalOutcome = await participant.GetOutcomeAsync(proposalTurn);
        Assert.Equal("confirmation_required", proposalOutcome!.Value.GetProperty("category").GetString());
        var token = proposalOutcome.Value.GetProperty("payload").GetProperty("token").GetString()!;

        var response = await participant.StartNewConversationAsync();
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clearedPendingConfirmation").GetBoolean());

        // The exact code they were holding no longer means anything, and saying it does not execute
        // the change they walked away from.
        var afterReset = await participant.SubmitAcceptedTurnAsync("native-reset-4", $"confirm {token}");
        await ProcessUntilQuietAsync();
        var afterOutcome = await participant.GetOutcomeAsync(afterReset);
        Assert.NotEqual("completed", afterOutcome!.Value.GetProperty("category").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var proposal = await db.ConfirmationProposals.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(ProposalStatus.ConversationReset), proposal.Status);
    }

    [Fact]
    public async Task Starting_a_new_conversation_leaves_a_waiting_file_import_exactly_where_it_was()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Importing Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Importing Warehouse");

        var csv = new ByteArrayContent(Encoding.UTF8.GetBytes("Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"));
        csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        var validate = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent { { csv, "file", "stock.csv" } },
            },
            withCsrf: true);

        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var proposalId = (await validate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("proposalId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await participant.StartNewConversationAsync()).StatusCode);

        // An Initial Import proposal is bound to a Participant and an Inventory, not to a
        // ChannelConversation: it is a browser file workflow that never became a Turn. "Clears pending
        // clarification/confirmation" means the one conversational proposal, so throwing away a file
        // the Participant already uploaded and previewed would be destroying work the reset never
        // promised to touch.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var stored = await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposalId);
        Assert.Equal(nameof(ImportProposalStatus.Pending), stored.Status);
    }

    [Fact]
    public async Task Starting_a_new_conversation_requires_the_csrf_token()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Forging Participant");

        var response = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/conversation/new"), withCsrf: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Starting_a_new_conversation_requires_an_active_tenant_member()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var response = await http.PostAsync("/api/conversation/new", content: null);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"An unauthenticated reset must never succeed, but it returned {response.StatusCode}.");
    }

    [Fact]
    public async Task Work_accepted_before_the_reset_still_completes_in_the_conversation_it_belonged_to()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Overlapping Participant");
        await participant.CreateAndSelectInventoryAsync("Overlapping Warehouse");

        var before = await participant.SubmitAcceptedTurnAsync("native-reset-5", "list stock");
        Assert.Equal(HttpStatusCode.OK, (await participant.StartNewConversationAsync()).StatusCode);
        var after = await participant.SubmitAcceptedTurnAsync("native-reset-6", "list stock");

        await ProcessUntilQuietAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var rows = await db.InboxEntries.AsNoTracking()
            .Where(e => e.TurnId == before || e.TurnId == after)
            .ToDictionaryAsync(e => e.TurnId);

        Assert.NotEqual(
            rows[before].FoundryConversationId,
            rows[after].FoundryConversationId);
        Assert.Equal(rows[before].FoundryConversationGeneration + 1, rows[after].FoundryConversationGeneration);

        // The reset changed which conversation NEW work joins. It did not abandon work already
        // accepted, and it did not break the queue that work is waiting in.
        Assert.NotNull(await participant.GetOutcomeAsync(before));
        Assert.NotNull(await participant.GetOutcomeAsync(after));
    }

    [Fact]
    public async Task A_change_proposed_by_work_from_before_the_reset_can_never_be_confirmed_after_it()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Superseded Participant");
        await participant.CreateAndSelectInventoryAsync("Superseded Warehouse");

        await participant.SubmitAcceptedTurnAsync("native-reset-7", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        // Accepted, then reset, then processed. The proposal this Turn asks for is created entirely
        // AFTER the reset committed, so the rotation's own transactional settle never saw it. This is
        // exactly the interleaving D10 exists for, end to end over HTTP.
        //
        // A BATCH, deliberately: StockChangeSetService confirms whenever a change set carries more
        // than one change, whatever each change is on its own, so this asks for confirmation without
        // depending on any particular prior Stock state. (A single `forget stock Steel Bolts` would
        // not: Forget refuses stock still on hand with forget_requires_zero_quantity and proposes
        // nothing, so this test would prove nothing.)
        var stale = await participant.SubmitAcceptedTurnAsync(
            "native-reset-8", "change stock: add Steel Bolts quantity 1; add Brass Rivets quantity 2");
        Assert.Equal(HttpStatusCode.OK, (await participant.StartNewConversationAsync()).StatusCode);
        await ProcessUntilQuietAsync();

        // The Turn did reach the confirmation path - the proposal below exists precisely because it
        // did - but it is never offered as one: a confirmation decided in a conversation the
        // Participant has already left is answered as the conflict it now is, carrying no token,
        // because handing back a bearer secret that is already settled would only render a prompt
        // that can never succeed. There is therefore no token to try, and trying an invented one
        // would prove nothing about this Turn.
        var staleOutcome = await participant.GetOutcomeAsync(stale);
        Assert.Equal("conflict", staleOutcome!.Value.GetProperty("category").GetString());
        Assert.Equal(ConfirmationProposalLifecycle.ConversationResetCode, staleOutcome.Value.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, staleOutcome.Value.GetProperty("payload").ValueKind);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        // Nothing was changed and nothing was left confirmable, and the proposal is settled as what it
        // is: work belonging to a conversation the Participant ended. That a proposal exists at all is
        // what makes this test non-vacuous - the batch really did produce something confirmable, and
        // the reset really is what stopped it.
        Assert.Equal(
            nameof(ProposalStatus.ConversationReset),
            (await db.ConfirmationProposals.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(4m, (await db.StockEntries.AsNoTracking().SingleAsync()).Quantity);
    }

    private async Task<Domain.Turns.FoundryConversationBinding> SingleBindingAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var row = await db.FoundryConversationBindings.AsNoTracking().SingleAsync();

        return new Domain.Turns.FoundryConversationBinding
        {
            ParticipantId = new ParticipantId(row.ParticipantId),
            ChannelConversationId = new Domain.Turns.ChannelConversationId(row.ChannelConversationId),
            FoundryConversationId = new Domain.Turns.FoundryConversationId(row.FoundryConversationId),
            Generation = row.Generation,
            CreatedAt = row.CreatedAt,
        };
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
```

Add `using System.Net.Http.Json;`, `using System.Net.Http.Headers;`, and `using System.Text;` to the file header, and `using MultiChannelAgent.Application.Inventories;` for the `conversation_reset` code the last test names. If `ImportProposalEntity.Status` is stored as something other than the `ImportProposalStatus` name, or the validate response names its proposal differently, use whatever `ImportEndpointsHttpTests` already asserts - do not invent a second vocabulary for the same rows.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~ConversationRotationHttpTests`
Expected: FAIL, all 8 - `/api/conversation/new` is not mapped yet, so the SPA fallback route answers instead: `Assert.Equal() Failure: Expected: OK, Actual: MethodNotAllowed` (the fallback matches the path for GET and HEAD only), and the tests that read the reset's body fail parsing `index.html` as JSON.

- [ ] **Step 3: Write the endpoint**

Create `src/MultiChannelAgent.Host/Endpoints/ConversationEndpoints.cs`:

```csharp
using System.Security.Claims;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the signed-in web channel's conversation lifecycle endpoint.
///
/// The request body is deliberately empty: the Participant and the ChannelConversation being reset
/// are always trusted context - the authenticated principal and this browser profile's own web
/// conversation cookie - so there is nothing a caller could send that would not be a way to reset
/// someone else's conversation.
/// </summary>
public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/conversation/new", async (
                HttpContext httpContext,
                ClaimsPrincipal user,
                ConversationRotationService rotationService,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var view = await rotationService.RotateAsync(
                    user.GetParticipantId(),
                    WebConversationCookie.EnsureId(httpContext),
                    timeProvider.GetUtcNow(),
                    cancellationToken);

                return Results.Ok(view);
            })
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}
```

In `src/MultiChannelAgent.Host/Program.cs`, add immediately after `app.MapInventoryEventEndpoints();`:

```csharp
app.MapConversationEndpoints();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~ConversationRotationHttpTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Host/Endpoints/ConversationEndpoints.cs src/MultiChannelAgent.Host/Program.cs \
        tests/MultiChannelAgent.IntegrationTests/ConversationRotationHttpTests.cs
git commit -m "feat: add the New conversation endpoint for the signed-in web channel"
```

---

## Task 13: One browser profile, many tabs - the shared-conversation scenario

**Files:**
- Test: `tests/MultiChannelAgent.IntegrationTests/SharedBrowserProfileScenario.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/SharedBrowserProfileScenario.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// One browser profile with several tabs open, against the real HTTP application boundary backed by
/// SQLite (fast, Docker-free).
///
/// Everything #35 promises about tabs, refreshes, and restarts reduces to one question: do two
/// clients that share the browser profile's cookies share one ChannelConversation, one FIFO queue,
/// and one view of every Turn in it? A page refresh, a browser restart, and a second tab are all the
/// same thing to the server - a new client presenting the same 400-day web conversation cookie - so
/// proving it for a second tab proves it for all three.
/// </summary>
public sealed class SharedBrowserProfileScenario : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Two_tabs_of_one_browser_profile_share_exactly_one_channel_conversation()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Multi Tab Participant");
        await firstTab.CreateAndSelectInventoryAsync("Shared Tab Warehouse");
        var secondTab = firstTab.OpenAnotherTab();

        await firstTab.SubmitAcceptedTurnAsync("native-tab-1", "list stock");
        await secondTab.SubmitAcceptedTurnAsync("native-tab-2", "list stock");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var conversations = await db.InboxEntries.AsNoTracking()
            .Select(e => e.ChannelConversationId)
            .Distinct()
            .ToListAsync();

        Assert.Single(conversations);
    }

    [Fact]
    public async Task Turns_submitted_from_different_tabs_queue_in_one_shared_first_in_first_out_order()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Queueing Participant");
        await firstTab.CreateAndSelectInventoryAsync("Queued Warehouse");
        var secondTab = firstTab.OpenAnotherTab();

        var first = await firstTab.SubmitAcceptedTurnAsync("native-fifo-1", "add stock Steel Bolts quantity 1");
        var second = await secondTab.SubmitAcceptedTurnAsync("native-fifo-2", "add stock Steel Bolts quantity 1");
        var third = await firstTab.SubmitAcceptedTurnAsync("native-fifo-3", "add stock Steel Bolts quantity 1");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var order = await db.InboxEntries.AsNoTracking()
            .OrderBy(e => e.ConversationSequence)
            .Select(e => e.TurnId)
            .ToListAsync();

        Assert.Equal(new[] { first, second, third }, order);
    }

    [Fact]
    public async Task A_second_tab_can_watch_a_turn_the_first_tab_submitted()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Watching Tabs Participant");
        await firstTab.CreateAndSelectInventoryAsync("Watched Tab Warehouse");
        var secondTab = firstTab.OpenAnotherTab();

        var turnId = await firstTab.SubmitAcceptedTurnAsync("native-tab-watch", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await secondTab.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await reader.ReadAsync(5, timeout.Token);
        Assert.Equal("outcome", events[^1].Name);
    }

    [Fact]
    public async Task Resubmitting_the_same_native_message_after_a_lost_response_returns_the_recorded_outcome_and_mutates_nothing_twice()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Recovering Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Recovered Warehouse");

        // The browser sent this once and never learned the answer - a dropped connection, a closed
        // laptop lid. On the next load it sends the very same native message id.
        var first = await participant.SubmitAcceptedTurnAsync("native-recover-1", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        var resubmission = await participant.SubmitTurnAsync("native-recover-1", "add stock Steel Bolts quantity 4");

        Assert.Equal(HttpStatusCode.OK, resubmission.StatusCode);
        var recorded = await resubmission.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first, recorded.GetProperty("turnId").GetGuid());

        await ProcessUntilQuietAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        Assert.Equal(1, await db.InboxEntries.AsNoTracking().CountAsync());

        // The mutation happened exactly once. This is what "disconnect recovery never resubmits
        // unknown mutation-capable work" actually means at the boundary: even the worst case - a
        // client that genuinely does not know whether its POST arrived - can only converge on the one
        // Turn that was recorded.
        var entry = await db.StockEntries.AsNoTracking().SingleAsync(e => e.InventoryId == inventoryId);
        Assert.Equal(4m, entry.Quantity);
    }

    [Fact]
    public async Task Reconnecting_to_a_turn_after_a_disconnect_replays_the_same_events_in_the_same_order()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Replaying Participant");
        await participant.CreateAndSelectInventoryAsync("Replayed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-replay-1", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);

        using var firstConnection = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var firstReader = await ServerSentEventReader.OpenAsync(firstConnection, timeout.Token);
        var firstEvents = await firstReader.ReadAsync(5, timeout.Token);

        using var secondConnection = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var secondReader = await ServerSentEventReader.OpenAsync(secondConnection, timeout.Token);
        var secondEvents = await secondReader.ReadAsync(5, timeout.Token);

        Assert.Equal(firstEvents.Select(e => (e.Id, e.Name, e.Data)), secondEvents.Select(e => (e.Id, e.Name, e.Data)));
    }

    [Fact]
    public async Task Browsing_another_inventory_never_changes_which_one_the_conversation_is_using()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Browsing Participant");
        var active = await participant.CreateAndSelectInventoryAsync("Active Warehouse");
        var other = await participant.CreateAndSelectInventoryAsync("Other Warehouse");
        await participant.SelectInventoryAsync(active);

        // Everything a Participant can do while looking around: list what they may see, read the
        // authoritative Stock projection of a different Inventory, read its references.
        Assert.Equal(HttpStatusCode.OK, (await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/inventories"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{other}/stock"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{other}/units"))).StatusCode);

        var bootstrap = (await participant.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(active.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());
    }

    [Fact]
    public async Task Using_an_inventory_in_this_conversation_switches_it_and_records_the_switch()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Switching Participant");
        var first = await participant.CreateAndSelectInventoryAsync("First Warehouse");
        var second = await participant.CreateAndSelectInventoryAsync("Second Warehouse");

        await participant.SelectInventoryAsync(first);
        await participant.SelectInventoryAsync(second);

        var bootstrap = (await participant.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(second.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var selection = await db.ActiveInventorySelections.AsNoTracking().SingleAsync();
        Assert.Equal(second, selection.InventoryId);
    }

    [Fact]
    public async Task A_switch_in_one_tab_is_the_switch_every_tab_of_that_browser_profile_sees()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Sharing Participant");
        var first = await firstTab.CreateAndSelectInventoryAsync("Tab One Warehouse");
        var second = await firstTab.CreateAndSelectInventoryAsync("Tab Two Warehouse");
        await firstTab.SelectInventoryAsync(first);

        var secondTab = firstTab.OpenAnotherTab();
        await secondTab.SelectInventoryAsync(second);

        var bootstrap = (await firstTab.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(second.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
```

Remove the unused `using MultiChannelAgent.Domain.Turns;` if the compiler flags it; `TreatWarningsAsErrors` makes an unused using a build failure.

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~SharedBrowserProfileScenario`
Expected: PASS, 8 tests. Every behaviour here is already implemented by Tasks 6-11 plus what shipped before this ticket - this task exists to prove the acceptance criteria hold together rather than only in isolation. If any test fails, the failure is a real defect in an earlier task, not a missing feature to add here.

- [ ] **Step 3: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/SharedBrowserProfileScenario.cs
git commit -m "test: prove one browser profile shares one conversation, queue, and stream"
```

---

## Task 14: The real SQL Server scenario, including concurrency

**Files:**
- Test: `tests/MultiChannelAgent.IntegrationTests/WebConversationContinuitySqlScenarioTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/WebConversationContinuitySqlScenarioTests.cs`:

```csharp
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The parts of this ticket that only a real database can prove: that the three new migrations apply
/// cleanly to a SQL Server schema built by every production migration before them, that the version
/// bump the persistence seam performs is safe under two concurrent writers, and that a conversation
/// reset racing a Turn acceptance always leaves both in a state that genuinely existed.
///
/// Backed by an ephemeral SQL Server container with production EF Core migrations applied, exactly
/// like every other SQL-backed scenario in this project.
/// </summary>
public sealed class WebConversationContinuitySqlScenarioTests : SqlIntegrationTestBase
{
    private const int DeadlockVictimErrorNumber = 1205;
    private const string SchemaBeforeWebConversationContinuity = "20260904125923_AddInventoryAuditOccurredAtTicks";
    private static readonly DateTimeOffset LegacyAcceptedAt = new(2026, 9, 4, 22, 50, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task Idempotent_upgrade_script_backfills_existing_inventory_and_turn_rows()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var participantId = Guid.NewGuid();
        var inventoryId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var foundryConversationId = Guid.NewGuid();
        const string channelConversationId = "web:legacy-profile";

        using (var scope = Factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            var migrator = db.Database.GetService<IMigrator>();

            await migrator.MigrateAsync(SchemaBeforeWebConversationContinuity);
            await SeedLegacyConversationAsync(
                db,
                participantId,
                inventoryId,
                turnId,
                channelConversationId,
                foundryConversationId);

            var script = migrator.GenerateScript(
                SchemaBeforeWebConversationContinuity,
                options: MigrationsSqlGenerationOptions.Idempotent);
            await ExecuteSqlScriptAsync(db, script);
        }

        using var verifyScope = Factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        Assert.Empty(await verifyDb.Database.GetPendingMigrationsAsync());
        Assert.Equal(
            0L,
            (await verifyDb.InventoryVersions.AsNoTracking().SingleAsync(v => v.InventoryId == inventoryId)).Version);
        Assert.Empty(await verifyDb.TurnProgressEvents.AsNoTracking().ToListAsync());

        var entry = await verifyDb.InboxEntries.AsNoTracking().SingleAsync(i => i.TurnId == turnId);
        Assert.Equal(foundryConversationId, entry.FoundryConversationId);
        Assert.Equal(1, entry.FoundryConversationGeneration);
    }

    [SkippableFact]
    public async Task A_turn_processed_against_real_sql_publishes_progress_a_version_and_a_resumable_stream()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Streaming SQL Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Streamed SQL Warehouse");

        var turnId = await participant.SubmitAcceptedTurnAsync("native-sql-1", "add stock Steel Bolts quantity 3");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var events = await reader.ReadAsync(5, timeout.Token);

        Assert.Equal(["accepted", "processing", "part", "part", "outcome"], events.Select(e => e.Name));

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.True((await db.InventoryVersions.AsNoTracking().SingleAsync(v => v.InventoryId == inventoryId)).Version > 0L);
    }

    [SkippableFact]
    public async Task Two_genuinely_concurrent_commits_in_one_inventory_both_publish_and_neither_loses_its_bump()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Concurrent SQL Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Concurrent SQL Warehouse");

        // Read AFTER all setup, because setup publishes too: creating the Inventory seeds version 0,
        // and every audited change since - a Membership grant, for instance - has already bumped it.
        // Asserting against an assumed zero here would be asserting against the setup, not the seam.
        var baseline = await VersionAsync(inventoryId);

        // Two independent DI scopes, each with its own DbContext and its own transaction, started
        // before either is awaited. This is the concurrency the name claims: nothing serializes them
        // at the application boundary, so they meet at the database exactly as two replicas would.
        async Task CommitAuditedChangeAsync(string outcomeCode)
        {
            using var scope = Factory!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

            var occurredAt = DateTimeOffset.UtcNow;
            db.InventoryAudits.Add(new InventoryAuditEntity
            {
                Id = Guid.NewGuid(),
                EventType = nameof(AuditEventType.StockAdded),
                ActorKind = nameof(AuditActorKind.Participant),
                ActorId = participant.ParticipantIdentifier,
                InventoryId = inventoryId,
                SubjectParticipantId = null,
                OutcomeCode = outcomeCode,
                OccurredAtUtc = occurredAt,
                OccurredAtUtcTicks = occurredAt.UtcTicks,
                ExpiresAtUtc = occurredAt.AddDays(90),
            });

            await db.SaveChangesAsync();
        }

        var first = CommitAuditedChangeAsync("concurrent-a");
        var second = CommitAuditedChangeAsync("concurrent-b");
        await Task.WhenAll(first, second);

        // Two committed changes, two versions. A lost update - the failure a read-then-write counter
        // would have - would show up here as baseline + 1.
        Assert.Equal(baseline + 2, await VersionAsync(inventoryId));
    }

    [SkippableFact]
    public async Task Granting_membership_is_itself_an_audited_change_and_publishes_a_version()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var ownerHttp = ConversationTestClient.CreateHttpsClient(Factory!);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Granting SQL Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Granting SQL Warehouse");

        var editorHttp = ConversationTestClient.CreateHttpsClient(Factory!);
        var editor = await ConversationTestClient.SignInAsync(editorHttp, "Granted SQL Editor");

        var before = await VersionAsync(inventoryId);
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");

        // Recorded here so no other test has to rediscover it: governance is an audited change, so it
        // publishes exactly like a stock change does. Any test that counts versions must therefore
        // count from a baseline it captured, not from zero.
        Assert.Equal(before + 1, await VersionAsync(inventoryId));
    }

    [SkippableFact]
    public async Task Two_concurrent_resets_of_one_conversation_advance_two_generations_and_never_collide()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Racing Reset Participant");
        await participant.CreateAndSelectInventoryAsync("Racing Reset Warehouse");
        var secondTab = participant.OpenAnotherTab();

        // Both requests are in flight before either is awaited, so this is a real race at the database.
        var statuses = await RunConcurrentlyRetryingDeadlockVictimAsync(
            participant.StartNewConversationAsync, secondTab.StartNewConversationAsync);

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var binding = await db.FoundryConversationBindings.AsNoTracking().SingleAsync();

        Assert.Equal(3, binding.Generation);
    }

    [SkippableFact]
    public async Task A_reset_racing_an_acceptance_always_stamps_the_turn_with_a_generation_that_really_existed()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available for the SQL Server-backed scenario.");

        var http = ConversationTestClient.CreateHttpsClient(Factory!);
        var participant = await ConversationTestClient.SignInAsync(http, "Racing Acceptance Participant");
        await participant.CreateAndSelectInventoryAsync("Racing Acceptance Warehouse");
        var secondTab = participant.OpenAnotherTab();

        var submission = participant.SubmitTurnAsync("native-sql-reset-race", "list stock");
        var reset = secondTab.StartNewConversationAsync();

        var statuses = await SettleConcurrentRequestsAsync(submission, reset);
        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.OK], statuses);

        await ProcessUntilQuietAsync();

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var entry = await db.InboxEntries.AsNoTracking().SingleAsync();
        var binding = await db.FoundryConversationBindings.AsNoTracking().SingleAsync();

        // Whichever order they committed in, the Turn carries a generation that genuinely existed:
        // either the one before the reset, or the one the reset created. What it can never be is
        // unset, or a generation nobody ever established.
        Assert.NotNull(entry.FoundryConversationGeneration);
        Assert.InRange(entry.FoundryConversationGeneration!.Value, binding.Generation - 1, binding.Generation);
        Assert.NotNull(entry.FoundryConversationId);

        // And it still reached a terminal Outcome. A reset never abandons accepted work.
        Assert.NotNull(await db.Outcomes.AsNoTracking().FirstOrDefaultAsync(o => o.TurnId == entry.TurnId));
    }

    /// <summary>
    /// Starts every attempt before awaiting any of them, so they genuinely overlap, and re-runs any
    /// attempt SQL Server chose as a deadlock victim. A deadlock is the engine resolving contention,
    /// not the application misbehaving - the shipped reference administration concurrency tests treat
    /// it the same way - but the victim's work did NOT happen, so it is actually retried rather than
    /// pretended to have succeeded. Fabricating a success would leave the caller asserting a
    /// generation that was never reached.
    /// </summary>
    private static async Task<IReadOnlyList<HttpStatusCode>> RunConcurrentlyRetryingDeadlockVictimAsync(
        params Func<Task<HttpResponseMessage>>[] attempts)
    {
        var settled = await SettleAsync(attempts.Select(attempt => attempt()).ToList());
        var statuses = new List<HttpStatusCode>(attempts.Length);

        for (var index = 0; index < settled.Count; index++)
        {
            if (settled[index].Status is { } status)
            {
                statuses.Add(status);
                continue;
            }

            // Only SQL Server error 1205 - "chosen as the deadlock victim" - is retried; every other
            // fault is rethrown with its original stack, so this can never quietly absorb a real
            // failure or turn one into a second request nobody asked for.
            var fault = settled[index].Fault!;
            if (DeadlockVictim(fault) is null)
            {
                ExceptionDispatchInfo.Throw(fault);
            }

            using var retried = await attempts[index]();
            statuses.Add(retried.StatusCode);
        }

        return statuses;
    }

    /// <summary>
    /// Awaits requests that were already in flight and reports their status codes in the order they
    /// were started, rethrowing the first fault only once every one of them has settled.
    /// </summary>
    private static async Task<IReadOnlyList<HttpStatusCode>> SettleConcurrentRequestsAsync(
        params Task<HttpResponseMessage>[] started)
    {
        var settled = await SettleAsync(started);

        foreach (var request in settled)
        {
            if (request.Fault is { } fault)
            {
                ExceptionDispatchInfo.Throw(fault);
            }
        }

        return settled.Select(request => request.Status!.Value).ToList();
    }

    /// <summary>
    /// Awaits every started request to completion, disposing each response the moment its status has
    /// been read, and reports what each one did.
    ///
    /// Nothing is decided about any request until all of them have settled, because deciding earlier
    /// is what leaks: a fault in the first would otherwise abandon the response the second is still
    /// producing, and an <see cref="HttpResponseMessage"/> nobody disposes holds its buffered body
    /// open for the rest of the run. Only the status code escapes, so no live response can.
    /// </summary>
    private static async Task<IReadOnlyList<SettledRequest>> SettleAsync(
        IReadOnlyList<Task<HttpResponseMessage>> started)
    {
        var settled = new List<SettledRequest>(started.Count);

        foreach (var request in started)
        {
            try
            {
                using var response = await request;
                settled.Add(new SettledRequest(response.StatusCode, null));
            }
            catch (Exception exception)
            {
                settled.Add(new SettledRequest(null, exception));
            }
        }

        return settled;
    }

    /// <summary>What one started request did: the status it answered with, or the fault it raised.</summary>
    private readonly record struct SettledRequest(HttpStatusCode? Status, Exception? Fault);

    private static SqlException? DeadlockVictim(Exception exception) => exception switch
    {
        SqlException { Number: DeadlockVictimErrorNumber } deadlock => deadlock,
        { InnerException: { } inner } => DeadlockVictim(inner),
        _ => null,
    };

    private async Task<long> VersionAsync(Guid inventoryId)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return (await db.InventoryVersions.AsNoTracking().SingleAsync(v => v.InventoryId == inventoryId)).Version;
    }

    private static async Task SeedLegacyConversationAsync(
        MultiChannelAgentDbContext db,
        Guid participantId,
        Guid inventoryId,
        Guid turnId,
        string channelConversationId,
        Guid foundryConversationId)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Participants (Id, DisplayName, CreatedAt, UpdatedAt, IsActive)
             VALUES ({participantId}, 'Legacy Participant', {LegacyAcceptedAt}, {LegacyAcceptedAt}, 1);
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Inventories (Id, Name, NormalizedName, CreatedByParticipantId, ClientRequestId, CreatedAt)
             VALUES ({inventoryId}, 'Legacy Warehouse', 'legacy warehouse', {participantId}, 'legacy-create', {LegacyAcceptedAt});
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO FoundryConversationBindings
                 (ParticipantId, ChannelConversationId, FoundryConversationId, Generation, CreatedAt)
             VALUES
                 ({participantId}, {channelConversationId}, {foundryConversationId}, 1, {LegacyAcceptedAt});
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO InboxEntries
                 (TurnId, NativeMessageId, ParticipantId, ChannelConversationId, ConversationSequence,
                  Channel, PrincipalKind, PrincipalSubject, PrincipalTenantId, Capabilities, Locale,
                  TraceId, WasInterrupted, ReceivedAt, ReceivedAtTicks, CreatedAt, Status)
             VALUES
                 ({turnId}, 'legacy-native-message', {participantId}, {channelConversationId}, 1,
                  'web', 'EntraUser', {participantId.ToString()}, NULL, 7, 'en-US',
                  'legacy-trace', 0, {LegacyAcceptedAt}, {LegacyAcceptedAt.UtcTicks}, {LegacyAcceptedAt}, 'Pending');
             """);
    }

    private static async Task ExecuteSqlScriptAsync(MultiChannelAgentDbContext db, string script)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*;?\s*$", RegexOptions.Multiline))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await db.Database.ExecuteSqlRawAsync(batch);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = Factory!.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
```

Delete any `using` the compiler reports as unused - `TreatWarningsAsErrors` turns one into a build failure.

- [ ] **Step 2: Run the tests with Docker required**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests --filter FullyQualifiedName~WebConversationContinuitySqlScenarioTests`
Expected: PASS, 6 tests. If Docker is genuinely unavailable on this machine, run without the variable and confirm all 6 report as skipped - never as passed.

If `Two_concurrent_resets_of_one_conversation_advance_two_generations_and_never_collide` reports generation 2 instead of 3, the guarded update in `SqlConversationRotationStore` is not actually guarding: one reset overwrote the other rather than retrying. Fix the store, not the test.

- [ ] **Step 3: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/WebConversationContinuitySqlScenarioTests.cs
git commit -m "test: prove migrations, version bumps, and rotation races against real SQL Server"
```

---

## Task 15: Web runtime test tooling

**Files:**
- Modify: `src/web/package.json`
- Modify: `src/web/vite.config.ts`
- Create: `src/web/src/testing/setup.ts`
- Create: `src/web/src/testing/fakeEventSource.ts`
- Modify: `.github/workflows/ci.yml`
- Test: `src/web/src/testing/setup.test.ts`

- [ ] **Step 1: Install the tooling**

```bash
cd src/web
npm install --save-dev vitest@5 jsdom@30 @testing-library/react@16 @testing-library/user-event@14 @testing-library/jest-dom@7
```

Confirm `package.json`'s `devDependencies` now contains exactly those five additions and nothing else. Then add the script to `package.json`'s `"scripts"`, immediately after `"lint"`:

```json
    "test": "vitest run",
```

Justification for these five and no more, recorded here because a dependency added to a repository that had none needs one: almost every acceptance criterion in #35 is client runtime behaviour - reconnecting a stream, resuming a Turn across a reload, coordinating tabs, navigating an accessible tab list - and none of it can be asserted by `tsc` or a linter. Vitest reuses this project's existing Vite config and transform pipeline, so no second build system enters the repository. Playwright was deliberately not added: it needs browser binaries in CI and would only add value for true visual layout, which is CSS and is not what these tests assert.

- [ ] **Step 2: Configure the runner**

Replace `src/web/vite.config.ts` with:

```ts
import react from '@vitejs/plugin-react'
// `defineConfig` comes from `vitest/config` (a superset of Vite's) so the same config file can
// carry both the build/dev settings and the `test` block Vitest reads - without this, Vitest and
// Vite would need separate config files that could drift out of sync.
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/testing/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // Every stubbed global (installFakeEventSource uses `vi.stubGlobal`) is torn down between
    // tests automatically, so one test's fake `EventSource` can never leak into the next.
    unstubGlobals: true,
  },
})
```

`defineConfig` is imported from `vitest/config` rather than `vite` because only that re-export types the `test` key; it is otherwise the same function, so the build is unchanged.

- [ ] **Step 3: Write the setup file**

Create `src/web/src/testing/setup.ts`:

```ts
// Global Vitest setup, loaded once per test file via `vitest/config`'s `test.setupFiles`. Wires up
// jest-dom's matchers, isolates every test's DOM/localStorage/viewport from the ones around it,
// and fills in a couple of jsdom gaps (`matchMedia`, `crypto.randomUUID`) that our components rely
// on but jsdom does not implement (or only implements in newer versions than we can rely on).
import '@testing-library/jest-dom/vitest'

import { cleanup } from '@testing-library/react'
import { afterEach, beforeEach } from 'vitest'

/** A viewport width comfortably inside a desktop/pointer layout. */
export const DESKTOP_WIDTH = 1280

/** A viewport width comfortably inside a narrow/mobile layout. */
export const NARROW_WIDTH = 480

const MAX_WIDTH_QUERY = /\(max-width:\s*(\d+(?:\.\d+)?)px\)/
const MIN_WIDTH_QUERY = /\(min-width:\s*(\d+(?:\.\d+)?)px\)/

let currentWidth = DESKTOP_WIDTH

function matchesWidth(query: string, width: number): boolean {
  const maxWidth = query.match(MAX_WIDTH_QUERY)
  const minWidth = query.match(MIN_WIDTH_QUERY)

  // Fail closed: a query using neither supported token is unsupported, not a match.
  if (!maxWidth && !minWidth) {
    return false
  }

  if (maxWidth && width > Number(maxWidth[1])) {
    return false
  }

  if (minWidth && width < Number(minWidth[1])) {
    return false
  }

  return true
}

type ChangeListener = (event: MediaQueryListEvent) => void

/**
 * A structurally-typed `MediaQueryList`: it satisfies the DOM interface our components call
 * (`matches`, `addEventListener`/`removeEventListener`, the deprecated `addListener`/
 * `removeListener` pair some libraries still use, and `dispatchEvent`) without depending on
 * jsdom's own CSS engine, which does not evaluate media queries at all.
 */
class FakeMediaQueryList implements MediaQueryList {
  readonly media: string
  matches: boolean
  onchange: ((this: MediaQueryList, event: MediaQueryListEvent) => void) | null = null

  private readonly listeners = new Set<ChangeListener>()

  constructor(media: string) {
    this.media = media
    this.matches = matchesWidth(media, currentWidth)
  }

  addEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    if (type === 'change') {
      this.listeners.add(listener as ChangeListener)
    }
  }

  removeEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    if (type === 'change') {
      this.listeners.delete(listener as ChangeListener)
    }
  }

  addListener(listener: ChangeListener | null): void {
    if (listener) {
      this.listeners.add(listener)
    }
  }

  removeListener(listener: ChangeListener | null): void {
    if (listener) {
      this.listeners.delete(listener)
    }
  }

  dispatchEvent(event: Event): boolean {
    const changeEvent = event as MediaQueryListEvent
    this.onchange?.call(this, changeEvent)
    this.listeners.forEach((listener) => listener(changeEvent))
    return true
  }

  /** Re-evaluates `matches` against the new width and notifies listeners if it changed. */
  refresh(width: number): void {
    const matches = matchesWidth(this.media, width)

    if (matches === this.matches) {
      return
    }

    this.matches = matches
    this.dispatchEvent({ matches, media: this.media } as MediaQueryListEvent)
  }
}

const liveQueries = new Set<FakeMediaQueryList>()

function fakeMatchMedia(query: string): MediaQueryList {
  const mediaQueryList = new FakeMediaQueryList(query)
  liveQueries.add(mediaQueryList)
  return mediaQueryList
}

/**
 * Sets the viewport width every subsequent `window.matchMedia` max-width/min-width query is
 * evaluated against, and re-evaluates every already-created `MediaQueryList` so components
 * listening for viewport changes (via `addEventListener('change', ...)`) see one too.
 */
export function setViewportWidth(width: number): void {
  currentWidth = width
  window.innerWidth = width
  liveQueries.forEach((mediaQueryList) => mediaQueryList.refresh(width))
}

let deterministicUuidCount = 0

function deterministicRandomUUID(): `${string}-${string}-${string}-${string}-${string}` {
  deterministicUuidCount += 1
  const suffix = deterministicUuidCount.toString(16).padStart(12, '0')
  return `00000000-0000-4000-8000-${suffix}`
}

beforeEach(() => {
  liveQueries.clear()
  window.matchMedia = fakeMatchMedia
  setViewportWidth(DESKTOP_WIDTH)

  window.localStorage.clear()

  if (typeof window.crypto === 'undefined' || typeof window.crypto.randomUUID !== 'function') {
    deterministicUuidCount = 0
    window.crypto ??= {} as Crypto
    window.crypto.randomUUID = deterministicRandomUUID
  }
})

afterEach(() => {
  cleanup()
})
```

- [ ] **Step 4: Write the EventSource double**

Create `src/web/src/testing/fakeEventSource.ts`:

```ts
// Faithful enough to swap in for the browser's global `EventSource` in tests: components under
// test call `new EventSource(url)`, `addEventListener`, and `close()` exactly as they would against
// the real thing, and the test drives the stream forward with `emit`/`fail` instead of a real HTTP
// response. Deliberately imports no production-code types - it stands in for a Web Platform API,
// not for anything this application defines, so it must never drift out of sync with either.
import { vi } from 'vitest'

type SseListener = (event: MessageEvent<string>) => void

/** A minimal, structurally-compatible stand-in for the browser's `EventSource`. */
export class FakeEventSource {
  readonly url: string
  closed = false
  onerror: ((event: Event) => void) | null = null
  /**
   * Mirrors the real `EventSource.readyState`: 0 (`CONNECTING`), 1 (`OPEN`), or 2 (`CLOSED`). Starts
   * `OPEN`, since a test drives a source that has already connected rather than modelling the
   * initial handshake.
   */
  readyState = 1

  private readonly listeners = new Map<string, Set<SseListener>>()

  constructor(url: string) {
    this.url = url
  }

  addEventListener(type: string, listener: SseListener): void {
    let forType = this.listeners.get(type)
    if (!forType) {
      forType = new Set()
      this.listeners.set(type, forType)
    }

    forType.add(listener)
  }

  removeEventListener(type: string, listener: SseListener): void {
    this.listeners.get(type)?.delete(listener)
  }

  close(): void {
    this.closed = true
    this.readyState = 2
  }

  /**
   * Delivers a named SSE event to every listener registered for it, JSON-encoding `data` exactly
   * as the real backend does (see `ServerSentEvents.WriteEventAsync`), so a test passes a plain
   * value and the component under test still receives a JSON string in `event.data`.
   */
  emit(type: string, data: unknown, lastEventId = ''): void {
    const event = new MessageEvent(type, { data: JSON.stringify(data), lastEventId })
    this.listeners.get(type)?.forEach((listener) => listener(event))
  }

  /**
   * Simulates a transient connection failure - exactly how a real `EventSource` reports one while
   * it silently reconnects on its own: `readyState` drops to `CONNECTING` (0) and `onerror` fires,
   * but the stream is not over. Does not close the stream by itself, since a real `EventSource`
   * reconnects on its own after a transient error unless the server ends the stream.
   */
  fail(): void {
    this.readyState = 0
    this.onerror?.(new Event('error'))
  }

  /**
   * Simulates a permanent connection failure - a 401/403/404 response, or one that isn't
   * `text/event-stream` - after which a real `EventSource` sets `readyState` to `CLOSED` (2) and
   * never reconnects, while still reporting the failure through `onerror` rather than throwing.
   */
  failFatally(): void {
    this.readyState = 2
    this.onerror?.(new Event('error'))
  }
}

/**
 * Builds an `EventSource`-shaped factory function that records every instance it creates, so a
 * test can reach into `opened` to drive (or inspect) whichever stream the component under test has
 * open - including after a reconnect, which opens a new instance rather than reusing the old one.
 */
export function recordingEventStreamFactory(): {
  opened: FakeEventSource[]
  factory: (url: string) => FakeEventSource
} {
  const opened: FakeEventSource[] = []

  function factory(url: string): FakeEventSource {
    const source = new FakeEventSource(url)
    opened.push(source)
    return source
  }

  return { opened, factory }
}

/**
 * Replaces the global `EventSource` constructor with a recording fake for the current test.
 * `vite.config.ts` enables Vitest's `unstubGlobals` option, so the stub is undone automatically
 * before the next test runs - a caller never has to restore it itself.
 */
export function installFakeEventSource(): FakeEventSource[] {
  const { opened, factory } = recordingEventStreamFactory()
  vi.stubGlobal('EventSource', factory)
  return opened
}
```

- [ ] **Step 5: Write a test that proves the harness itself works**

Create `src/web/src/testing/setup.test.ts`:

```ts
import { describe, expect, it } from 'vitest'

import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './setup'

describe('test runtime setup', () => {
  it('resolves max-width and min-width media queries against the width set by setViewportWidth', () => {
    // `beforeEach` in setup.ts already put us at DESKTOP_WIDTH.
    expect(window.matchMedia(`(max-width: ${NARROW_WIDTH}px)`).matches).toBe(false)
    expect(window.matchMedia(`(min-width: ${DESKTOP_WIDTH}px)`).matches).toBe(true)

    setViewportWidth(NARROW_WIDTH)

    expect(window.matchMedia(`(max-width: ${NARROW_WIDTH}px)`).matches).toBe(true)
    expect(window.matchMedia(`(min-width: ${DESKTOP_WIDTH}px)`).matches).toBe(false)

    // Leave something behind for the next test to prove does not leak across tests.
    window.localStorage.setItem('leftover', 'from-a-previous-test')
  })

  it('treats an unsupported media query as non-matching instead of defaulting to true', () => {
    // `(width <= ...)` is neither the supported max-width nor min-width token, so a fail-closed
    // harness must report no match rather than silently matching every unrecognized query.
    expect(window.matchMedia(`(width <= ${NARROW_WIDTH}px)`).matches).toBe(false)
  })

  it('starts with empty localStorage even though the previous test wrote to it', () => {
    expect(window.localStorage.length).toBe(0)
    expect(window.localStorage.getItem('leftover')).toBeNull()
  })
})
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `cd src/web && npm test`
Expected: PASS, 3 tests. `fakeEventSource.ts` has no test of its own and imports nothing from the code under test, so it compiles on its own here and is exercised from Task 16 onwards.

- [ ] **Step 7: Add the CI gate**

In `.github/workflows/ci.yml`, in the `frontend` job, add between the `Lint` and `Build` steps:

```yaml
      - name: Test
        working-directory: src/web
        run: npm test
```

- [ ] **Step 8: Commit**

```bash
git add src/web/package.json src/web/package-lock.json src/web/vite.config.ts src/web/src/testing .github/workflows/ci.yml
git commit -m "chore: add Vitest and Testing Library for web runtime tests"
```

---

## Task 16: The typed per-Turn stream client

**Files:**
- Create: `src/web/src/turnStream.ts`
- Test: `src/web/src/turnStream.test.ts`

- [ ] **Step 1: Write the failing test**

Create `src/web/src/turnStream.test.ts`:

```ts
import { describe, expect, it, vi } from 'vitest'

import { recordingEventStreamFactory } from './testing/fakeEventSource'
import { TURN_EVENT_SEQUENCE, openTurnStream } from './turnStream'
import type { TurnStreamOutcomeEvent } from './turnStream'

describe('openTurnStream', () => {
  it('opens the per-Turn stream endpoint with no resume point on a first connection', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openTurnStream({ turnId: 'turn-1', factory })

    expect(opened).toHaveLength(1)
    expect(opened[0]?.url).toBe('/api/turns/turn-1/events')
  })

  it('resumes from a caller-supplied lastEventId via the query parameter', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openTurnStream({ turnId: 'turn-1', lastEventId: TURN_EVENT_SEQUENCE.processing, factory })

    expect(opened[0]?.url).toBe(`/api/turns/turn-1/events?lastEventId=${TURN_EVENT_SEQUENCE.processing}`)
  })

  it('dispatches each typed event to its own handler, retaining a data part payload and observing the outcome code', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onAccepted = vi.fn()
    const onProcessing = vi.fn()
    const onPart = vi.fn()
    const onOutcome = vi.fn()

    openTurnStream({
      turnId: 'turn-1',
      factory,
      handlers: { onAccepted, onProcessing, onPart, onOutcome },
    })

    const source = opened[0]!
    source.emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' }, String(TURN_EVENT_SEQUENCE.accepted))
    source.emit('processing', { turnId: 'turn-1', startedAt: '2026-09-05T00:00:01Z' }, String(TURN_EVENT_SEQUENCE.processing))
    source.emit(
      'part',
      { turnId: 'turn-1', order: 1, kind: 'text', text: 'Added 2 kg of flour.', payload: null },
      String(TURN_EVENT_SEQUENCE.firstPart),
    )
    const dataPayload = { version: 1, kind: 'stock_mutation', operation: 'add' }
    source.emit(
      'part',
      { turnId: 'turn-1', order: 2, kind: 'data', text: null, payload: dataPayload },
      String(TURN_EVENT_SEQUENCE.firstPart + 1),
    )
    source.emit(
      'outcome',
      {
        turnId: 'turn-1',
        status: 'completed',
        category: 'completed',
        code: 'stock_mutation_applied',
        summary: 'Added 2 kg of flour.',
        deliveries: [],
      } satisfies TurnStreamOutcomeEvent,
      String(TURN_EVENT_SEQUENCE.outcome),
    )

    expect(onAccepted).toHaveBeenCalledWith({ turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' })
    expect(onProcessing).toHaveBeenCalledWith({ turnId: 'turn-1', startedAt: '2026-09-05T00:00:01Z' })
    expect(onPart).toHaveBeenCalledTimes(2)
    expect(onPart).toHaveBeenNthCalledWith(2, { turnId: 'turn-1', order: 2, kind: 'data', text: null, payload: dataPayload })
    expect(onOutcome).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'stock_mutation_applied', deliveries: [] }),
    )
  })

  it('closes the source itself the instant the terminal outcome event arrives', () => {
    const { opened, factory } = recordingEventStreamFactory()

    openTurnStream({ turnId: 'turn-1', factory })

    const source = opened[0]!
    expect(source.closed).toBe(false)

    source.emit(
      'outcome',
      { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'ok', summary: '', deliveries: [] },
      String(TURN_EVENT_SEQUENCE.outcome),
    )

    expect(source.closed).toBe(true)
  })

  it('advances lastEventId() to the greatest valid event identity received so far', () => {
    const { opened, factory } = recordingEventStreamFactory()

    const stream = openTurnStream({ turnId: 'turn-1', factory })
    expect(stream.lastEventId()).toBe(0)

    const source = opened[0]!
    source.emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' }, String(TURN_EVENT_SEQUENCE.accepted))
    expect(stream.lastEventId()).toBe(TURN_EVENT_SEQUENCE.accepted)

    source.emit('processing', { turnId: 'turn-1', startedAt: '2026-09-05T00:00:01Z' }, String(TURN_EVENT_SEQUENCE.processing))
    expect(stream.lastEventId()).toBe(TURN_EVENT_SEQUENCE.processing)
  })

  it('reports a connection failure through onDisconnected exactly once without closing, so the browser can reconnect on its own', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onDisconnected = vi.fn()
    const onFailed = vi.fn()

    openTurnStream({ turnId: 'turn-1', factory, handlers: { onDisconnected, onFailed } })

    const source = opened[0]!
    source.fail()

    expect(onDisconnected).toHaveBeenCalledTimes(1)
    expect(onFailed).not.toHaveBeenCalled()
    expect(source.closed).toBe(false)
  })

  it('reports a permanent connection failure through onFailed exactly once, closing the source and suppressing onDisconnected', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onDisconnected = vi.fn()
    const onFailed = vi.fn()

    openTurnStream({ turnId: 'turn-1', factory, handlers: { onDisconnected, onFailed } })

    const source = opened[0]!
    source.failFatally()

    expect(onFailed).toHaveBeenCalledTimes(1)
    expect(onDisconnected).not.toHaveBeenCalled()
    expect(source.closed).toBe(true)
  })

  it('closes the source before invoking the terminal handler, so a throwing onOutcome still leaves the stream closed', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const boom = new Error('boom')
    const onOutcome = vi.fn(() => {
      throw boom
    })

    openTurnStream({ turnId: 'turn-1', factory, handlers: { onOutcome } })

    const source = opened[0]!

    expect(() =>
      source.emit(
        'outcome',
        { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'ok', summary: '', deliveries: [] },
        String(TURN_EVENT_SEQUENCE.outcome),
      ),
    ).toThrow(boom)

    expect(source.closed).toBe(true)
  })

  it('rejects a stale, non-numeric, non-integer, or unsafe-integer event id, never regressing or corrupting lastEventId()', () => {
    const { opened, factory } = recordingEventStreamFactory()

    const stream = openTurnStream({ turnId: 'turn-1', factory })
    const source = opened[0]!

    source.emit('part', { turnId: 'turn-1', order: 1, kind: 'text', text: 'seed', payload: null }, '100')
    expect(stream.lastEventId()).toBe(100)

    const rejectedIds = ['2', 'not-a-number', '2.5', String(Number.MAX_SAFE_INTEGER + 2)]
    for (const rawId of rejectedIds) {
      source.emit('part', { turnId: 'turn-1', order: 2, kind: 'text', text: 'noise', payload: null }, rawId)
      expect(stream.lastEventId()).toBe(100)
    }
  })

  it('suppresses events observed after the caller closes the stream, and closes the underlying source', () => {
    const { opened, factory } = recordingEventStreamFactory()
    const onAccepted = vi.fn()

    const stream = openTurnStream({ turnId: 'turn-1', factory, handlers: { onAccepted } })

    const source = opened[0]!
    stream.close()
    expect(source.closed).toBe(true)

    source.emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-05T00:00:00Z' }, String(TURN_EVENT_SEQUENCE.accepted))

    expect(onAccepted).not.toHaveBeenCalled()
    expect(stream.lastEventId()).toBe(0)
  })
})
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/web && npm test`
Expected: FAIL with `Failed to resolve import "./turnStream"`.

- [ ] **Step 3: Write the implementation**

Create `src/web/src/turnStream.ts`:

```ts
import type { DeliveryView, TurnOutcomePayload } from './turnsApi'

/**
 * The same fixed sequence identities `TurnEventSequence` issues server-side (see
 * `MultiChannelAgent.Domain.Turns.TurnEventSequence`). `firstPart` is the lowest of a sparse block
 * reserved for response parts - a stream never carries more than a couple, but the identities stay
 * stable even if more are added later.
 */
export const TURN_EVENT_SEQUENCE = {
  accepted: 1,
  processing: 2,
  firstPart: 100,
  outcome: 1_000_000,
} as const

/**
 * The numeric value of the browser's `EventSource.readyState` once a connection has permanently
 * failed. Written as a literal rather than read off the global `EventSource` constructor so this
 * module never depends on that global being defined - a fake source in tests supplies the same
 * number without needing to exist as a real constructor.
 */
const EVENT_SOURCE_CLOSED = 2

export interface TurnAcceptedEvent {
  turnId: string
  receivedAt: string
}

export interface TurnProcessingEvent {
  turnId: string
  startedAt: string
}

/** One channel-neutral piece of the answer. Exactly one of `text` and `payload` is ever present. */
export interface TurnResponsePartEvent {
  turnId: string
  order: number
  kind: 'text' | 'data'
  text: string | null
  payload: TurnOutcomePayload | null
}

export interface TurnStreamOutcomeEvent {
  turnId: string
  status: string
  category: string
  code: string
  summary: string
  deliveries: DeliveryView[]
}

export interface TurnStreamHandlers {
  onAccepted?: (event: TurnAcceptedEvent) => void
  onProcessing?: (event: TurnProcessingEvent) => void
  onPart?: (event: TurnResponsePartEvent) => void
  onOutcome?: (event: TurnStreamOutcomeEvent) => void
  /**
   * Called on a transient connection failure. Purely informational: the browser's own EventSource
   * reconnects by itself with `Last-Event-ID` set to the last identity it observed, so a caller
   * only ever uses this to reflect connection state, never to react by reconnecting itself.
   */
  onDisconnected?: () => void
  /**
   * Called once the connection has failed permanently - the browser has set `readyState` to
   * `CLOSED` and given up reconnecting on its own (a 401/403/404 response, or a response that is
   * not `text/event-stream`, all end this way). Unlike `onDisconnected`, the stream is over: a
   * caller needs to surface this rather than silently keep waiting for events that will never come.
   */
  onFailed?: () => void
}

/** The minimal shape of the browser's `EventSource` this client depends on. */
export interface EventStreamSource {
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void
  close(): void
  onerror: ((event: Event) => void) | null
  /** 0 (`CONNECTING`), 1 (`OPEN`), or 2 (`CLOSED`) - mirrors the real `EventSource.readyState`. */
  readonly readyState: number
}

export type EventStreamFactory = (url: string) => EventStreamSource

export interface TurnStream {
  /** The greatest event identity observed so far - what a caller persists to resume later. */
  lastEventId(): number
  close(): void
}

export interface OpenTurnStreamOptions {
  turnId: string
  /** The identity to resume from, or 0 (the default) to open the stream from its beginning. */
  lastEventId?: number
  handlers?: TurnStreamHandlers
  /** Defaults to the real `EventSource`; tests supply a fake instead. */
  factory?: EventStreamFactory
}

/**
 * Opens one Turn's finite, resumable event stream and dispatches each named SSE event to its typed
 * handler.
 *
 * A positive `lastEventId` is sent as the `lastEventId` query parameter rather than a header,
 * because this always represents a deliberate resume (a fresh page load, or a caller reopening
 * after a gap) - the header is reserved for the browser's own automatic reconnect, which this
 * function never has to construct by hand. On the very first connection there is nothing to resume,
 * so the URL carries no query at all.
 */
export function openTurnStream(options: OpenTurnStreamOptions): TurnStream {
  const { turnId, handlers = {} } = options
  const factory = options.factory ?? ((url: string) => new EventSource(url))
  let seen = options.lastEventId ?? 0
  let closed = false

  const url = seen > 0 ? `/api/turns/${turnId}/events?lastEventId=${seen}` : `/api/turns/${turnId}/events`
  const source = factory(url)

  // Only a safe-integer identity greater than what is already recorded ever moves the resume
  // point - an out-of-order, non-numeric, non-integer, or unsafely-large id (which should never
  // happen against this server, but a client should not trust the wire blindly) simply leaves it
  // where it was. `Number.isSafeInteger` rejects non-integers (a fractional id makes no sense for
  // a monotonic counter) and anything beyond `MAX_SAFE_INTEGER`, where `Number` can no longer tell
  // adjacent integers apart - accepting one of those could silently corrupt the resume point with
  // a value neither this id nor any real one the server ever issued.
  function observe(rawId: string): void {
    const id = Number(rawId)
    if (Number.isSafeInteger(id) && id > seen) {
      seen = id
    }
  }

  function on<T>(type: string, handler: ((event: T) => void) | undefined, terminal = false): void {
    source.addEventListener(type, (event) => {
      // A caller's own close() must suppress anything still in flight - the underlying source's
      // close() does not stop events already queued for delivery.
      if (closed) {
        return
      }

      observe(event.lastEventId)

      if (terminal) {
        // Closed before the payload is parsed or the handler is invoked, not after: the terminal
        // event is this stream's last regardless of whether `JSON.parse` throws on a malformed
        // payload or the handler itself throws, and closing here is what stops the browser's
        // EventSource from reconnecting to a Turn with nothing left to say.
        closed = true
        source.close()
      }

      handler?.(JSON.parse(event.data) as T)
    })
  }

  on<TurnAcceptedEvent>('accepted', handlers.onAccepted)
  on<TurnProcessingEvent>('processing', handlers.onProcessing)
  on<TurnResponsePartEvent>('part', handlers.onPart)
  on<TurnStreamOutcomeEvent>('outcome', handlers.onOutcome, true)

  source.onerror = () => {
    if (closed) {
      return
    }

    if (source.readyState === EVENT_SOURCE_CLOSED) {
      // The browser has already given up - permanently, not just for this attempt - so the stream
      // really is over. Closing here is idempotent (the browser put it in this state already) but
      // guarantees the caller-visible `closed` flag agrees with reality regardless.
      closed = true
      source.close()
      handlers.onFailed?.()
      return
    }

    // Deliberately does not close: a transient failure is exactly what the browser's own
    // reconnect (using Last-Event-ID) is for, and closing here would defeat that reconnect.
    handlers.onDisconnected?.()
  }

  return {
    lastEventId: () => seen,
    close: () => {
      closed = true
      source.close()
    },
  }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/web && npm test`
Expected: PASS, 13 tests (3 existing plus 10 for `openTurnStream`).

- [ ] **Step 5: Check types and lint**

Run: `cd src/web && npx tsc -b && npm run lint`
Expected: no output from `tsc`, and no errors from oxlint.

- [ ] **Step 6: Commit**

```bash
git add src/web/src/turnStream.ts src/web/src/turnStream.test.ts src/web/src/testing/fakeEventSource.ts
git commit -m "feat: add the typed resumable per-Turn stream client"
```

---

## Task 17: Browser-profile conversation continuity

**Files:**
- Create: `src/web/src/conversationStorage.ts`
- Test: `src/web/src/conversationStorage.test.ts`

- [ ] **Step 1: Write the failing test**

Create `src/web/src/conversationStorage.test.ts`:

```ts
import { describe, expect, it, vi } from 'vitest'

import {
  clearInFlightTurn,
  readInFlightTurn,
  rememberSubmission,
  rememberTurnId,
  subscribeToConversationChanges,
} from './conversationStorage'

const CONVERSATION = 'web-conversation-1'
const OTHER_CONVERSATION = 'web-conversation-2'

/** The literal storage key layout, kept independent of the module's own (private) prefix constant
 * so a test that pokes raw `localStorage` proves the real on-disk shape rather than whatever the
 * module happens to compute internally. */
function rawKeyFor(webConversationId: string): string {
  return `mca.conversation.${webConversationId}`
}

describe('readInFlightTurn', () => {
  it('returns null when no record has ever been stored for the conversation', () => {
    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('returns null and does not throw for a corrupt, non-JSON value', () => {
    localStorage.setItem(rawKeyFor(CONVERSATION), 'not-json{')

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('returns null for well-formed JSON that is the wrong shape', () => {
    localStorage.setItem(rawKeyFor(CONVERSATION), JSON.stringify({ turnId: 42 }))

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('returns null for well-formed JSON with all three valid fields plus an unexpected extra field', () => {
    localStorage.setItem(
      rawKeyFor(CONVERSATION),
      JSON.stringify({
        nativeMessageId: 'native-1',
        contentText: 'list stock',
        turnId: 'turn-1',
        secret: 'confirmation-token-should-never-be-here',
      }),
    )

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })

  it('keeps records for different conversations isolated', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(OTHER_CONVERSATION)).toBeNull()
  })
})

describe('rememberSubmission', () => {
  it('stores the native message id and content text with turnId null before any HTTP response arrives', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    expect(readInFlightTurn(CONVERSATION)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: null,
    })
  })

  it('serializes exactly contentText, nativeMessageId, and turnId - never an answer, payload, or token', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    const raw = localStorage.getItem(rawKeyFor(CONVERSATION))
    expect(raw).not.toBeNull()
    const parsed = JSON.parse(raw!) as Record<string, unknown>
    expect(Object.keys(parsed).sort()).toEqual(['contentText', 'nativeMessageId', 'turnId'])
  })
})

describe('rememberTurnId', () => {
  it('updates the existing record with the turn id once the HTTP response arrives', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })

    rememberTurnId(CONVERSATION, 'native-1', 'turn-1')

    expect(readInFlightTurn(CONVERSATION)).toEqual({
      nativeMessageId: 'native-1',
      contentText: 'list stock',
      turnId: 'turn-1',
    })
  })

  it('keeps the newer submission untouched when a turn id arrives for an older, already-superseded submission', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-A', contentText: 'list stock' })
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-B', contentText: 'find bolts' })

    rememberTurnId(CONVERSATION, 'native-A', 'turn-A')

    expect(readInFlightTurn(CONVERSATION)).toEqual({
      nativeMessageId: 'native-B',
      contentText: 'find bolts',
      turnId: null,
    })
  })

  it('does not silently create a record when there is no existing submission', () => {
    rememberTurnId(CONVERSATION, 'native-1', 'turn-1')

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })
})

describe('clearInFlightTurn', () => {
  it('removes the record for the conversation', () => {
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' })
    rememberTurnId(CONVERSATION, 'native-1', 'turn-1')

    clearInFlightTurn(CONVERSATION)

    expect(readInFlightTurn(CONVERSATION)).toBeNull()
  })
})

describe('subscribeToConversationChanges', () => {
  it('invokes the callback for a storage event carrying exactly this conversation key', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION) }))

    expect(onChanged).toHaveBeenCalledTimes(1)
  })

  it('ignores storage events for another conversation and for unrelated keys', () => {
    const onChanged = vi.fn()
    subscribeToConversationChanges(CONVERSATION, onChanged)

    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(OTHER_CONVERSATION) }))
    window.dispatchEvent(new StorageEvent('storage', { key: 'unrelated.key' }))

    expect(onChanged).not.toHaveBeenCalled()
  })

  it('stops delivering notifications once unsubscribed', () => {
    const onChanged = vi.fn()
    const unsubscribe = subscribeToConversationChanges(CONVERSATION, onChanged)

    unsubscribe()
    window.dispatchEvent(new StorageEvent('storage', { key: rawKeyFor(CONVERSATION) }))

    expect(onChanged).not.toHaveBeenCalled()
  })
})
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/web && npm test`
Expected: FAIL with `Failed to resolve import "./conversationStorage"`.

- [ ] **Step 3: Write the implementation**

Create `src/web/src/conversationStorage.ts`:

```ts
/**
 * Persists one browser profile's single in-flight Turn for one web conversation, so a page
 * refresh, a crashed tab, or a second tab open on the same browser profile can find - and resume
 * watching - whatever Turn is still outstanding. Scoped under `localStorage` deliberately: that
 * scope matches the lifetime and reach of the long-lived, HttpOnly `WebConversationCookie` this
 * record is keyed against, so the two always agree on which browser profile they describe.
 *
 * This is a resumption breadcrumb, not a cache of the conversation itself - it never stores an
 * answer, a payload, an auth token, or any other secret or confirmation-bearing value. Only the
 * three fields below are ever written or read back.
 */

const KEY_PREFIX = 'mca.conversation.'

/** One browser profile's outstanding Turn for a single web conversation. */
export interface InFlightTurn {
  nativeMessageId: string
  contentText: string
  turnId: string | null
}

function keyFor(webConversationId: string): string {
  return `${KEY_PREFIX}${webConversationId}`
}

/** Runtime type guard: rejects anything that is not exactly the three-field shape above - no fewer
 * and no more - so a corrupted or foreign value read back from storage is treated as absent rather
 * than trusted. Rejecting rather than normalizing away extra keys matters here: it guarantees an
 * unknown or secret field can never be re-persisted by a later write that spreads the record read
 * back in (e.g. `rememberTurnId`), because that write never sees the record at all. */
function isInFlightTurn(value: unknown): value is InFlightTurn {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const record = value as Record<string, unknown>

  return (
    Object.keys(record).length === 3 &&
    typeof record.nativeMessageId === 'string' &&
    typeof record.contentText === 'string' &&
    (record.turnId === null || typeof record.turnId === 'string')
  )
}

/** Reads the in-flight Turn for a conversation, or null if there is none - including when the
 * stored value is corrupt (not JSON) or well-formed JSON of the wrong shape. Never throws. */
export function readInFlightTurn(webConversationId: string): InFlightTurn | null {
  const raw = localStorage.getItem(keyFor(webConversationId))

  if (raw === null) {
    return null
  }

  let parsed: unknown

  try {
    parsed = JSON.parse(raw)
  } catch {
    return null
  }

  return isInFlightTurn(parsed) ? parsed : null
}

/** Records a just-submitted message as the conversation's in-flight Turn, overwriting whatever
 * was there before. `turnId` is always null here: it is filled in later, by `rememberTurnId`, once
 * the HTTP response naming the Turn actually arrives. */
export function rememberSubmission(
  webConversationId: string,
  submission: { nativeMessageId: string; contentText: string },
): void {
  const record: InFlightTurn = { ...submission, turnId: null }
  localStorage.setItem(keyFor(webConversationId), JSON.stringify(record))
}

/** Fills in the Turn id on an existing in-flight record once the HTTP response names it. Requires
 * the `nativeMessageId` that response belongs to, and does nothing unless a valid existing record
 * is stored AND its `nativeMessageId` equals the one supplied here - it never silently creates a
 * record from just a turn id (missing the `nativeMessageId`/`contentText` a resumed UI needs), and
 * it never lets a response for a submission that has since been superseded overwrite whatever the
 * browser profile submitted next. Without that check, a stale response arriving after a second
 * submission - in this tab or another tab of the same browser profile - would stamp its turn id
 * onto the newer, unrelated record. */
export function rememberTurnId(webConversationId: string, nativeMessageId: string, turnId: string): void {
  const existing = readInFlightTurn(webConversationId)

  if (existing === null || existing.nativeMessageId !== nativeMessageId) {
    return
  }

  const record: InFlightTurn = { ...existing, turnId }
  localStorage.setItem(keyFor(webConversationId), JSON.stringify(record))
}

/** Removes the in-flight Turn record for a conversation, e.g. once its outcome has been
 * delivered and there is nothing left to resume. */
export function clearInFlightTurn(webConversationId: string): void {
  localStorage.removeItem(keyFor(webConversationId))
}

/** Notifies `onChanged` whenever another tab on the same browser profile changes this exact
 * conversation's in-flight Turn record (the browser's `storage` event only ever fires in other
 * tabs, never the one that made the change). Returns an unsubscribe function. */
export function subscribeToConversationChanges(webConversationId: string, onChanged: () => void): () => void {
  const key = keyFor(webConversationId)

  const listener = (event: StorageEvent) => {
    if (event.key === key) {
      onChanged()
    }
  }

  window.addEventListener('storage', listener)

  return () => window.removeEventListener('storage', listener)
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/web && npm test`
Expected: PASS, all 14 tests from Step 1 (which includes a hardening case proving `rememberTurnId` never silently creates a record when there is no existing submission to update, a case proving a stale response for a superseded submission never overwrites the submission that replaced it, and a case proving a record with a valid shape plus an unexpected extra field is rejected outright rather than partly trusted).

- [ ] **Step 5: Commit**

```bash
git add src/web/src/conversationStorage.ts src/web/src/conversationStorage.test.ts
git commit -m "feat: persist one browser profile's in-flight Turn without storing any secret"
```

---

## Task 18: The typed Inventory invalidation stream client

**Files:**
- Create: `src/web/src/inventoryStream.ts`
- Test: `src/web/src/inventoryStream.test.ts`

- [ ] **Step 1: Write the failing test**

Create `src/web/src/inventoryStream.test.ts`:

```ts
import { describe, expect, it, vi } from 'vitest';
import { recordingEventStreamFactory } from './testing/fakeEventSource';
import { openInventoryStream } from './inventoryStream';
import type { InventoryVersions } from './inventoryStream';

describe('openInventoryStream', () => {
  it('opens the Participant-level stream', () => {
    const { opened, factory } = recordingEventStreamFactory();

    openInventoryStream({ onVersions: () => {}, createSource: factory });

    expect(opened).toHaveLength(1);
    expect(opened[0].url).toBe('/api/inventory-events');
  });

  it('reports the whole snapshot the moment it connects', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onVersions = vi.fn();

    openInventoryStream({ onVersions, createSource: factory });
    opened[0].emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 3 },
        { inventoryId: 'inventory-2', version: 0 },
      ],
    });

    expect(onVersions).toHaveBeenCalledWith({ 'inventory-1': 3, 'inventory-2': 0 });
  });

  it('does not let a caller corrupt internal state by mutating the object handed to onVersions', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onVersions = vi.fn();

    openInventoryStream({ onVersions, createSource: factory });
    opened[0].emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] });

    // Reaches into the very object the stream handed back and pollutes it - exactly what a fresh
    // object literal on every publish is meant to make harmless.
    const firstReceived = onVersions.mock.calls[0][0] as InventoryVersions;
    firstReceived['inventory-1'] = 999;
    firstReceived['inventory-2'] = 42;

    opened[0].emit('changed', { inventoryId: 'inventory-1', version: 4 });

    // Had the mutation reached the internal picture, this would also carry the stray
    // "inventory-2": 42 the caller injected above.
    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-1': 4 });
  });

  it('folds each later change into the picture it already had', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onVersions = vi.fn();

    openInventoryStream({ onVersions, createSource: factory });
    opened[0].emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] });
    opened[0].emit('changed', { inventoryId: 'inventory-1', version: 4 });

    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-1': 4 });
  });

  it('drops an Inventory the Participant may no longer see', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onVersions = vi.fn();

    openInventoryStream({ onVersions, createSource: factory });
    opened[0].emit('snapshot', {
      inventories: [
        { inventoryId: 'inventory-1', version: 3 },
        { inventoryId: 'inventory-2', version: 1 },
      ],
    });
    opened[0].emit('revoked', { inventoryId: 'inventory-2' });

    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-1': 3 });
  });

  it('replaces the whole picture on the next snapshot, so a reconnect is a total resynchronization', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onVersions = vi.fn();

    openInventoryStream({ onVersions, createSource: factory });
    opened[0].emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] });
    opened[0].emit('snapshot', { inventories: [{ inventoryId: 'inventory-2', version: 9 }] });

    expect(onVersions).toHaveBeenLastCalledWith({ 'inventory-2': 9 });
  });

  it('stops reporting once the caller closes it', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onVersions = vi.fn();

    const stream = openInventoryStream({ onVersions, createSource: factory });
    stream.close();
    opened[0].emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 3 }] });

    expect(onVersions).not.toHaveBeenCalled();
    expect(opened[0].closed).toBe(true);
  });

  it('reports a permanent connection failure through onFailed exactly once and closes the source', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onFailed = vi.fn();

    openInventoryStream({ onVersions: () => {}, onFailed, createSource: factory });
    opened[0].failFatally();

    expect(onFailed).toHaveBeenCalledTimes(1);
    expect(opened[0].closed).toBe(true);
  });

  it('ignores a transient connection failure, leaving the source open for the browser to reconnect on its own', () => {
    const { opened, factory } = recordingEventStreamFactory();
    const onFailed = vi.fn();

    openInventoryStream({ onVersions: () => {}, onFailed, createSource: factory });
    opened[0].fail();

    expect(onFailed).not.toHaveBeenCalled();
    expect(opened[0].closed).toBe(false);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/web && npm test`
Expected: FAIL with `Failed to resolve import "./inventoryStream"`.

- [ ] **Step 3: Write the implementation**

Create `src/web/src/inventoryStream.ts`:

```ts
import type { EventStreamFactory } from './turnStream';

/**
 * The numeric value of the browser's `EventSource.readyState` once a connection has permanently
 * failed. Written as a literal rather than read off the global `EventSource` constructor so this
 * module never depends on that global being defined - a fake source in tests supplies the same
 * number without needing to exist as a real constructor.
 */
const EVENT_SOURCE_CLOSED = 2;

/** The version each Inventory this Participant may see is currently at, keyed by Inventory id. */
export type InventoryVersions = Record<string, number>;

interface InventoryVersionWire {
  inventoryId: string;
  version: number;
}

interface InventorySnapshotWire {
  inventories: InventoryVersionWire[];
}

interface InventoryRevokedWire {
  inventoryId: string;
}

export interface OpenInventoryStreamOptions {
  /** Called with the complete current picture every time any part of it changes. */
  onVersions: (versions: InventoryVersions) => void;
  /** Called once the connection has failed permanently and this stream is over. */
  onFailed?: () => void;
  createSource?: EventStreamFactory;
}

export interface InventoryStream {
  close: () => void;
}

/**
 * Watches this Participant's Inventory invalidation stream: which Inventories they may see, and what
 * version each is at, changed by anyone through any channel.
 *
 * The server sends a complete snapshot the moment a connection opens and only differences after that,
 * so this client never needs a resume point - and correspondingly the server issues no event
 * identities for it. A reconnect is therefore a total resynchronization, which is stronger than
 * replaying a cursor would be: nothing can be missed while a tab is closed, and a Membership granted
 * or revoked in the meantime simply arrives in the next snapshot.
 *
 * Every callback hands back the whole picture rather than the delta, because that is what a caller
 * actually renders from - and because folding deltas is exactly the sort of bookkeeping a component
 * should never have to get right. The picture is stored internally as a plain `Record` keyed by
 * Inventory id (a server-issued GUID in practice), and every publish clones it with a fresh object
 * literal - never `Object.assign` onto or otherwise mutating the internal copy - so a caller can
 * never corrupt this stream's state through the reference it was handed.
 */
export function openInventoryStream({
  onVersions,
  onFailed,
  createSource = defaultEventStreamFactory,
}: OpenInventoryStreamOptions): InventoryStream {
  const source = createSource('/api/inventory-events');
  let versions: InventoryVersions = {};
  let closed = false;

  const publish = () => onVersions({ ...versions });

  source.onerror = () => {
    if (closed) {
      return;
    }

    if (source.readyState !== EVENT_SOURCE_CLOSED) {
      // Transient: the browser reconnects on its own, and the next snapshot resynchronizes -
      // closing here, or reporting an error, would defeat that reconnect for no reason.
      return;
    }

    // The browser has already given up permanently (a 401/403/404 response, or one that isn't
    // `text/event-stream`) and will not reconnect on its own. Closing here is idempotent - the
    // browser put `readyState` in this state already - but guarantees the caller-visible `closed`
    // flag agrees with reality regardless.
    closed = true;
    source.close();
    onFailed?.();
  };

  source.addEventListener('snapshot', (event) => {
    if (closed) {
      return;
    }

    const snapshot = JSON.parse(event.data) as InventorySnapshotWire;

    // Replaced, never merged: a snapshot is the whole truth, so an Inventory missing from it is one
    // this Participant may no longer see.
    versions = Object.fromEntries(snapshot.inventories.map((i) => [i.inventoryId, i.version]));
    publish();
  });

  source.addEventListener('changed', (event) => {
    if (closed) {
      return;
    }

    const changed = JSON.parse(event.data) as InventoryVersionWire;
    versions = { ...versions, [changed.inventoryId]: changed.version };
    publish();
  });

  source.addEventListener('revoked', (event) => {
    if (closed) {
      return;
    }

    const revoked = JSON.parse(event.data) as InventoryRevokedWire;
    const remaining = { ...versions };
    delete remaining[revoked.inventoryId];
    versions = remaining;
    publish();
  });

  return {
    close: () => {
      closed = true;
      source.close();
    },
  };
}

const defaultEventStreamFactory: EventStreamFactory = (url) => new EventSource(url);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/web && npm test`
Expected: PASS, 9 new tests.

- [ ] **Step 5: Check types and lint**

Run: `cd src/web && npx tsc -b && npm run lint`
Expected: no output from `tsc`, and no errors from oxlint.

- [ ] **Step 6: Commit**

```bash
git add src/web/src/inventoryStream.ts src/web/src/inventoryStream.test.ts
git commit -m "feat: add the Participant-level Inventory invalidation stream client"
```

---

## Task 19: The responsive, accessible workspace panel

**Files:**
- Create: `src/web/src/useMediaQuery.ts`
- Create: `src/web/src/WorkspacePanel.tsx`
- Modify: `src/web/src/index.css`
- Test: `src/web/src/WorkspacePanel.test.tsx`
- Test: `src/web/src/useMediaQuery.test.ts`

- [ ] **Step 1: Write the failing test**

Create `src/web/src/WorkspacePanel.test.tsx`:

```tsx
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './testing/setup';
import WorkspacePanel from './WorkspacePanel';

function renderPanel() {
  return render(
    <WorkspacePanel
      conversation={<p>Conversation content</p>}
      workspace={<p>Workspace content</p>}
    />,
  );
}

/** A probe that proves whether it was remounted: its count only ever survives if React kept it. */
function StatefulProbe({ testId }: { testId: string }) {
  const [count, setCount] = useState(0);

  return (
    <div data-testid={testId}>
      <button type="button" onClick={() => setCount((value) => value + 1)}>
        {count}
      </button>
    </div>
  );
}

describe('WorkspacePanel on a desktop viewport', () => {
  it('puts the conversation in the main landmark and the workspace beside it', () => {
    setViewportWidth(DESKTOP_WIDTH);
    renderPanel();

    expect(screen.getByRole('main')).toHaveTextContent('Conversation content');
    expect(screen.getByRole('complementary', { name: 'Inventory workspace' })).toHaveTextContent(
      'Workspace content',
    );
  });

  it('shows both at once, with no tabs to navigate', () => {
    setViewportWidth(DESKTOP_WIDTH);
    renderPanel();

    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.getByText('Conversation content')).toBeVisible();
    expect(screen.getByText('Workspace content')).toBeVisible();
  });

  it('reads the conversation first, so assistive technology reaches it before the workspace', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { container } = renderPanel();

    const text = container.textContent ?? '';
    expect(text.indexOf('Conversation content')).toBeLessThan(text.indexOf('Workspace content'));
  });
});

describe('WorkspacePanel on a narrow viewport', () => {
  it('keeps the page inside a main landmark, with the tab panel in it', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    // An explicit role replaces an element's implicit one, so putting role="tabpanel" on <main> would
    // delete the page's only main landmark at exactly the widths where skipping to content matters
    // most. The panel therefore lives inside main rather than being it.
    const main = screen.getByRole('main');
    expect(within(main).getByRole('tablist')).toBeInTheDocument();
    expect(within(main).getByRole('tabpanel')).toHaveTextContent('Conversation content');
  });

  it('offers an accessible tab list with the conversation selected', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const tabs = screen.getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['Conversation', 'Inventory']);
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
    expect(tabs[1]).toHaveAttribute('aria-selected', 'false');
  });

  it('hides the inactive panel from assistive technology and the tab order, without unmounting it', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    // Still mounted - so no in-flight state, stream, or focus it might hold is discarded - just
    // out of the accessibility tree and the tab order, which is what "shows only" has to mean here.
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Conversation content');
    expect(screen.getByText('Workspace content')).not.toBeVisible();
  });

  it('switches panels when a tab is chosen, hiding the other without unmounting it', async () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    await userEvent.click(screen.getByRole('tab', { name: 'Inventory' }));

    expect(screen.getByRole('tabpanel')).toHaveTextContent('Workspace content');
    expect(screen.getByRole('tab', { name: 'Inventory' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('Conversation content')).not.toBeVisible();
  });

  it('moves between tabs with the arrow keys, and only the selected tab is in the tab order', async () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const [conversationTab, inventoryTab] = screen.getAllByRole('tab');
    expect(conversationTab).toHaveAttribute('tabindex', '0');
    expect(inventoryTab).toHaveAttribute('tabindex', '-1');

    conversationTab.focus();
    await userEvent.keyboard('{ArrowRight}');

    expect(inventoryTab).toHaveAttribute('aria-selected', 'true');
    expect(inventoryTab).toHaveFocus();

    await userEvent.keyboard('{Home}');
    expect(conversationTab).toHaveAttribute('aria-selected', 'true');
    expect(conversationTab).toHaveFocus();
  });

  it('names each panel with the tab that controls it', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const panel = screen.getByRole('tabpanel');
    const tab = screen.getByRole('tab', { name: 'Conversation' });

    expect(panel).toHaveAttribute('aria-labelledby', tab.id);
    expect(tab).toHaveAttribute('aria-controls', panel.id);
  });

  it("keeps both panels present, so neither tab's aria-controls ever points at a missing element", () => {
    setViewportWidth(NARROW_WIDTH);
    const { container } = renderPanel();

    for (const tab of screen.getAllByRole('tab')) {
      const controlledId = tab.getAttribute('aria-controls');
      expect(controlledId).toBeTruthy();
      // Queried directly on the DOM, not through an accessibility-tree query, since the point is
      // that the id exists at all - including for the inactive panel, which is hidden but present.
      expect(container.querySelector(`#${controlledId}`)).not.toBeNull();
    }
  });

  it('keeps the conversation first in the document, so it stays the primary surface', () => {
    setViewportWidth(NARROW_WIDTH);
    renderPanel();

    const tabs = screen.getAllByRole('tab');
    expect(tabs[0]).toHaveTextContent('Conversation');
  });
});

describe('WorkspacePanel across a breakpoint transition', () => {
  it('keeps the conversation and workspace mounted - not remounted - when the viewport crosses the breakpoint and back', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    render(
      <WorkspacePanel
        conversation={<StatefulProbe testId="conversation-probe" />}
        workspace={<StatefulProbe testId="workspace-probe" />}
      />,
    );

    const conversationProbe = screen.getByTestId('conversation-probe');
    const workspaceProbe = screen.getByTestId('workspace-probe');

    await userEvent.click(within(conversationProbe).getByRole('button'));
    await userEvent.click(within(conversationProbe).getByRole('button'));
    await userEvent.click(within(workspaceProbe).getByRole('button'));

    expect(conversationProbe).toHaveTextContent('2');
    expect(workspaceProbe).toHaveTextContent('1');

    act(() => {
      setViewportWidth(NARROW_WIDTH);
    });

    // The layout genuinely changed to the narrow, tabbed arrangement...
    expect(screen.getByRole('tablist')).toBeInTheDocument();
    // ...yet both probes are the very same DOM nodes, with their state intact - proof neither
    // was unmounted and remounted by the transition.
    expect(screen.getByTestId('conversation-probe')).toBe(conversationProbe);
    expect(screen.getByTestId('workspace-probe')).toBe(workspaceProbe);
    expect(conversationProbe).toHaveTextContent('2');
    expect(workspaceProbe).toHaveTextContent('1');

    act(() => {
      setViewportWidth(DESKTOP_WIDTH);
    });

    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.getByTestId('conversation-probe')).toBe(conversationProbe);
    expect(screen.getByTestId('workspace-probe')).toBe(workspaceProbe);
    expect(conversationProbe).toHaveTextContent('2');
    expect(workspaceProbe).toHaveTextContent('1');
  });

  it('unsubscribes its media query listener when unmounted', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const originalMatchMedia = window.matchMedia;
    const subscriptions: { addSpy: ReturnType<typeof vi.spyOn>; removeSpy: ReturnType<typeof vi.spyOn> }[] = [];

    // useMediaQuery reads a fresh MediaQueryList on every render (for its snapshot) as well as one
    // to subscribe to, so every list this test sees is spied on, and the subscribed one - the one
    // that actually gets an `addEventListener` call - is picked out afterwards.
    window.matchMedia = (query: string) => {
      const list = originalMatchMedia(query);
      subscriptions.push({
        addSpy: vi.spyOn(list, 'addEventListener'),
        removeSpy: vi.spyOn(list, 'removeEventListener'),
      });
      return list;
    };

    const { unmount } = renderPanel();

    const subscribed = subscriptions.find(({ addSpy }) => addSpy.mock.calls.length > 0);
    expect(subscribed).toBeDefined();
    const [, listener] = subscribed!.addSpy.mock.calls[0];

    unmount();
    window.matchMedia = originalMatchMedia;

    expect(subscribed!.removeSpy).toHaveBeenCalledWith('change', listener);
  });
});

describe('WorkspacePanel preserving focus across a narrowing transition', () => {
  it('keeps a focused element inside the workspace panel visible and focused when narrowing', () => {
    setViewportWidth(DESKTOP_WIDTH);
    render(
      <WorkspacePanel
        conversation={<p>Conversation content</p>}
        workspace={<input aria-label="Workspace field" />}
      />,
    );

    const input = screen.getByLabelText('Workspace field');
    input.focus();
    expect(input).toHaveFocus();

    act(() => {
      setViewportWidth(NARROW_WIDTH);
    });

    // `selected` state defaults to 'conversation', so without a focus-aware correction the
    // workspace panel - the one actually holding focus - is exactly the one that would be hidden.
    expect(screen.getByRole('tab', { name: 'Inventory' })).toHaveAttribute('aria-selected', 'true');
    expect(input).toBeVisible();
    expect(document.activeElement).toBe(input);
  });

  it('keeps a focused element inside the conversation panel visible and focused when narrowing, even after a prior Inventory selection', async () => {
    setViewportWidth(NARROW_WIDTH);
    render(
      <WorkspacePanel
        conversation={<input aria-label="Conversation field" />}
        workspace={<p>Workspace content</p>}
      />,
    );

    // Select Inventory once while still narrow, so `selected` state is 'workspace' - stale once
    // the viewport widens and focus moves elsewhere.
    await userEvent.click(screen.getByRole('tab', { name: 'Inventory' }));
    expect(screen.getByRole('tab', { name: 'Inventory' })).toHaveAttribute('aria-selected', 'true');

    act(() => {
      setViewportWidth(DESKTOP_WIDTH);
    });

    const input = screen.getByLabelText('Conversation field');
    input.focus();
    expect(input).toHaveFocus();

    act(() => {
      setViewportWidth(NARROW_WIDTH);
    });

    // The stale 'workspace' selection must not win: focus is in the conversation panel now, so
    // that is the panel narrowing has to keep visible.
    expect(screen.getByRole('tab', { name: 'Conversation' })).toHaveAttribute('aria-selected', 'true');
    expect(input).toBeVisible();
    expect(document.activeElement).toBe(input);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/web && npm test`
Expected: FAIL with `Failed to resolve import "./WorkspacePanel"`.

- [ ] **Step 3: Write the media query hook, and a test for its subscription stability**

Create `src/web/src/useMediaQuery.ts`:

```ts
import { useCallback, useSyncExternalStore } from 'react';

/**
 * Whether the viewport currently matches a CSS media query, kept up to date as it changes.
 *
 * The layout has to be a real branch and not only a stylesheet: below the breakpoint the workspace
 * is behind a tab, and a tab whose panel is merely hidden with CSS is still in the accessibility
 * tree, still focusable, and still read out. Deciding it here means the DOM says what the screen
 * shows.
 *
 * Reads through `useSyncExternalStore` rather than an effect that calls `setState`: the browser's
 * `MediaQueryList` is the external store, and this is its canonical synchronization hook - the first
 * render already reflects the current viewport with no synchronize-after-paint flash, and no lint
 * warning about calling `setState` synchronously inside an effect.
 *
 * `subscribe` and `getSnapshot` are each memoized by `query` rather than passed as fresh closures:
 * `useSyncExternalStore` re-subscribes (tears down the old listener, adds a new one) whenever
 * `subscribe`'s identity changes, so an inline closure would tear down and recreate the listener on
 * every render of whatever calls this hook - including one caused by something this hook has
 * nothing to do with, like WorkspacePanel switching its selected tab.
 */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      const list = window.matchMedia(query);
      list.addEventListener('change', onStoreChange);

      return () => list.removeEventListener('change', onStoreChange);
    },
    [query],
  );

  const getSnapshot = useCallback(() => window.matchMedia(query).matches, [query]);

  return useSyncExternalStore(subscribe, getSnapshot);
}

/**
 * The one breakpoint this application has. Below it the conversation and the workspace cannot both
 * be usable at once, so the workspace moves behind a tab.
 */
export const NARROW_SCREEN_QUERY = '(max-width: 1023px)';
```

Create `src/web/src/useMediaQuery.test.ts`:

```ts
import { describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { DESKTOP_WIDTH, setViewportWidth } from './testing/setup';
import { useMediaQuery } from './useMediaQuery';

/**
 * Spies on every `MediaQueryList` `window.matchMedia` creates for the lifetime of this call, so
 * `addEventListener`/`removeEventListener` calls on each one can be inspected individually - some
 * lists are only ever read (`useSyncExternalStore`'s snapshot) and never subscribed to at all.
 */
function spyOnMatchMedia() {
  const originalMatchMedia = window.matchMedia;
  const lists: { list: MediaQueryList; addSpy: ReturnType<typeof vi.spyOn>; removeSpy: ReturnType<typeof vi.spyOn> }[] =
    [];

  window.matchMedia = (query: string) => {
    const list = originalMatchMedia(query);
    lists.push({
      list,
      addSpy: vi.spyOn(list, 'addEventListener'),
      removeSpy: vi.spyOn(list, 'removeEventListener'),
    });
    return list;
  };

  return {
    lists,
    subscribed: () => lists.find(({ addSpy }) => addSpy.mock.calls.length > 0),
    restore: () => {
      window.matchMedia = originalMatchMedia;
    },
  };
}

describe('useMediaQuery', () => {
  it('does not tear down and recreate its subscription on an unrelated rerender', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const spy = spyOnMatchMedia();

    const { rerender, unmount } = renderHook(({ query }) => useMediaQuery(query), {
      initialProps: { query: '(max-width: 1023px)' },
    });

    const subscribed = spy.subscribed();
    expect(subscribed).toBeDefined();
    expect(subscribed!.addSpy).toHaveBeenCalledTimes(1);

    // Rerendering with the very same query string is exactly what an unrelated state change inside
    // a consumer (WorkspacePanel switching its selected tab, say) looks like from this hook's side -
    // a stable subscribe/getSnapshot pair must not tear down and recreate the listener for it.
    rerender({ query: '(max-width: 1023px)' });
    rerender({ query: '(max-width: 1023px)' });

    expect(subscribed!.addSpy).toHaveBeenCalledTimes(1);
    expect(subscribed!.removeSpy).not.toHaveBeenCalled();

    unmount();
    expect(subscribed!.removeSpy).toHaveBeenCalledTimes(1);
    spy.restore();
  });

  it('tears down the old subscription and starts a new one when the query changes', () => {
    setViewportWidth(DESKTOP_WIDTH);
    const spy = spyOnMatchMedia();

    const { result, rerender, unmount } = renderHook(({ query }) => useMediaQuery(query), {
      initialProps: { query: '(max-width: 1023px)' },
    });

    // 1280px (DESKTOP_WIDTH) doesn't match max-width:1023px.
    expect(result.current).toBe(false);
    const first = spy.subscribed();
    expect(first).toBeDefined();

    rerender({ query: '(min-width: 100px)' });

    // The old listener is gone, a new one exists for the new query, and the returned value now
    // reflects that query instead of the stale one.
    expect(first!.removeSpy).toHaveBeenCalledTimes(1);
    const second = spy.lists.find(
      (entry) => entry !== first && entry.list.media === '(min-width: 100px)' && entry.addSpy.mock.calls.length > 0,
    );
    expect(second).toBeDefined();
    expect(second!.addSpy).toHaveBeenCalledTimes(1);
    // 1280px matches min-width:100px.
    expect(result.current).toBe(true);

    unmount();
    expect(second!.removeSpy).toHaveBeenCalledTimes(1);
    spy.restore();
  });
});
```

- [ ] **Step 4: Write the panel**

Create `src/web/src/WorkspacePanel.tsx`:

```tsx
import { useRef, useState } from 'react';
import { NARROW_SCREEN_QUERY, useMediaQuery } from './useMediaQuery';

interface WorkspacePanelProps {
  conversation: React.ReactNode;
  workspace: React.ReactNode;
}

const TABS = [
  { id: 'conversation', label: 'Conversation' },
  { id: 'workspace', label: 'Inventory' },
] as const;

type TabId = (typeof TABS)[number]['id'];

/**
 * The responsive frame: conversation primary, Inventory workspace beside it or behind a tab.
 *
 * Renders one tree shape at every width - a `main` landmark that always wraps the same three
 * children, in the same order: the tab list, the conversation panel, and the Inventory panel. Only
 * their attributes and visibility respond to the viewport, never their presence, so `conversation`
 * and `workspace` are never unmounted (and never lose their state, in-flight streams, or focus) by a
 * breakpoint change. On a wide viewport the tab list is inert and the Inventory panel is a
 * `complementary` landmark beside the conversation; below the breakpoint the Inventory panel becomes
 * an ARIA tabpanel and the tab list switches between them. Either way the inactive panel - if any -
 * is taken out of the accessibility tree and the tab order with the semantic `hidden` attribute
 * instead of being removed, so both panel ids always exist for the tabs' `aria-controls` to point at.
 *
 * The conversation panel is always first in document order, and its tab is selected by default, which
 * is what actually keeps it primary - CSS placement doesn't decide reading or default focus order.
 */
function WorkspacePanel({ conversation, workspace }: WorkspacePanelProps) {
  const isNarrow = useMediaQuery(NARROW_SCREEN_QUERY);
  const [selected, setSelected] = useState<TabId>('conversation');
  const tabRefs = useRef<Record<TabId, HTMLButtonElement | null>>({ conversation: null, workspace: null });
  const [wasNarrow, setWasNarrow] = useState(isNarrow);

  /*
   * Corrected synchronously during render - before this transition ever reaches the DOM - because
   * once a focused element's container is actually hidden, the browser may already have moved focus
   * to <body> by the time any effect could run, and there is no reclaiming it after the fact.
   * Comparing against `wasNarrow` (the *previous* render's viewport, not `selected` state) is what
   * tells a genuine desktop-to-narrow transition apart from an ordinary rerender while already
   * narrow, where `selected` alone has to keep governing - otherwise every rerender would re-detect
   * "just narrowed" and a deliberate tab click could never stick.
   *
   * This is the "adjust state while rendering" pattern React's own docs describe for exactly this
   * "compare against the last render" shape, using state - never a ref's `current` - so the read is
   * never stale: React discards this render's output and immediately retries with both `wasNarrow`
   * and `selected` already corrected, so every read of either below already sees the final value.
   * Looking the panels up by id rather than through a ref to their element keeps the focus check
   * itself a plain DOM read too - like `useMediaQuery` reading `matchMedia(...).matches` - rather
   * than a ref access.
   */
  if (isNarrow !== wasNarrow) {
    setWasNarrow(isNarrow);

    if (isNarrow) {
      const focusedPanel = TABS.map((tab) => tab.id).find((id) =>
        document.getElementById(`workspace-panel-${id}`)?.contains(document.activeElement),
      );

      if (focusedPanel && focusedPanel !== selected) {
        setSelected(focusedPanel);
      }
    }
  }

  const select = (id: TabId) => {
    setSelected(id);
    tabRefs.current[id]?.focus();
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    const index = TABS.findIndex((tab) => tab.id === selected);

    if (event.key === 'ArrowRight') {
      event.preventDefault();
      select(TABS[(index + 1) % TABS.length].id);
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      select(TABS[(index - 1 + TABS.length) % TABS.length].id);
    } else if (event.key === 'Home') {
      event.preventDefault();
      select(TABS[0].id);
    } else if (event.key === 'End') {
      event.preventDefault();
      select(TABS[TABS.length - 1].id);
    }
  };

  return (
    <main className={isNarrow ? 'workspace-layout workspace-layout-narrow' : 'workspace-layout'}>
      {/*
        Always rendered at this same position so its (dis)appearance from an assistive-technology
        or visual point of view never shifts the panels below it - only its role and content are
        conditional on the viewport, never its presence in the tree.
      */}
      <div
        role={isNarrow ? 'tablist' : undefined}
        aria-label={isNarrow ? 'Workspace sections' : undefined}
        hidden={!isNarrow}
        className="workspace-tabs"
      >
        {isNarrow &&
          TABS.map((tab) => (
            <button
              key={tab.id}
              id={`workspace-tab-${tab.id}`}
              ref={(element) => {
                tabRefs.current[tab.id] = element;
              }}
              type="button"
              role="tab"
              aria-selected={selected === tab.id}
              aria-controls={`workspace-panel-${tab.id}`}
              // Roving tab order: the tab list is one stop, and the arrow keys move within it.
              tabIndex={selected === tab.id ? 0 : -1}
              onClick={() => select(tab.id)}
              onKeyDown={onKeyDown}
            >
              {tab.label}
            </button>
          ))}
      </div>

      <div
        id="workspace-panel-conversation"
        className="workspace-conversation"
        role={isNarrow ? 'tabpanel' : undefined}
        aria-labelledby={isNarrow ? 'workspace-tab-conversation' : undefined}
        hidden={isNarrow && selected !== 'conversation'}
        tabIndex={isNarrow ? 0 : undefined}
      >
        {conversation}
      </div>

      <aside
        id="workspace-panel-workspace"
        className="workspace-panel"
        aria-label={isNarrow ? undefined : 'Inventory workspace'}
        role={isNarrow ? 'tabpanel' : undefined}
        aria-labelledby={isNarrow ? 'workspace-tab-workspace' : undefined}
        hidden={isNarrow && selected !== 'workspace'}
        tabIndex={isNarrow ? 0 : undefined}
      >
        {workspace}
      </aside>
    </main>
  );
}

export default WorkspacePanel;
```

- [ ] **Step 5: Add the layout styles**

Append to `src/web/src/index.css`:

```css
/*
  Conversation primary at every width. `main` is always the layout's own grid container and always
  has the same three children - the tab list, the conversation panel, and the Inventory panel - so a
  breakpoint change only ever changes their attributes and visibility, never which subtrees exist (see
  WorkspacePanel). On a wide viewport the tab list collapses out of the grid and the two panels sit
  side by side; below the breakpoint they stack behind the tab list instead.
*/
.workspace-layout {
  display: grid;
  grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
  gap: 24px;
  align-items: start;
  text-align: left;
  padding: 0 16px 32px;
}

.workspace-layout-narrow {
  grid-template-columns: minmax(0, 1fr);
}

.workspace-conversation,
.workspace-panel {
  min-width: 0;
}

.workspace-panel {
  border-inline-start: 1px solid var(--border);
  padding-inline-start: 24px;
}

.workspace-tabs {
  display: flex;
  gap: 8px;
  border-bottom: 1px solid var(--border);
  margin-bottom: 16px;
}

/*
  An author style always outranks the `[hidden]` attribute's own display:none, regardless of
  specificity, so the tab list's own `display: flex` needs this to actually disappear (and stop
  taking a grid cell) on a wide viewport - `hidden` alone still keeps it out of the accessibility
  tree and the tab order either way, but this keeps the visual result honest too.
*/
.workspace-tabs[hidden] {
  display: none;
}

.workspace-tabs [role='tab'] {
  background: none;
  border: 0;
  border-bottom: 2px solid transparent;
  color: var(--text);
  cursor: pointer;
  font: inherit;
  padding: 8px 12px;
}

.workspace-tabs [role='tab'][aria-selected='true'] {
  border-bottom-color: var(--accent);
  color: var(--text-h);
}

.workspace-tabs [role='tab']:focus-visible,
[role='tabpanel']:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

@media (max-width: 1023px) {
  .workspace-layout {
    grid-template-columns: minmax(0, 1fr);
  }

  .workspace-panel {
    border-inline-start: 0;
    padding-inline-start: 0;
  }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd src/web && npm test`
Expected: PASS, 17 new tests. (The first pass through this task only writes 10 - the desktop and narrow-viewport describes. A quality review then found that the desktop/narrow trees were structurally incompatible, so a breakpoint change unmounted and remounted `conversation` and `workspace`, discarding whatever state, in-flight stream, or focus they held; and that the old `useEffect`-based `useMediaQuery` was oxlint's only `react(set-state-in-effect)` warning in the web app. Steps 3-5 above already carry that fix - one stable tree shape, `hidden` instead of conditional unmounting, and `useSyncExternalStore` - and the test file above already carries the 3 tests that pin it down: the transition test proving both children's identity and state survive a round trip across the breakpoint, the unmount test proving the media query subscription is cleaned up, and the `aria-controls` test proving both panel ids are always present. A second quality review then found two more issues: `useMediaQuery` passed a fresh inline closure to `useSyncExternalStore` on every render, tearing the listener down and recreating it on every unrelated rerender of whatever calls it; and narrowing could hide whichever panel currently held focus, since `selected` state is not aware of where focus actually is. Step 3 above already carries the first fix - `subscribe` and `getSnapshot` memoized by `query` with `useCallback` - proved by `useMediaQuery.test.ts`'s 2 tests. Step 4 above already carries the second - comparing the viewport against the previous render's `wasNarrow` state and, on a genuine narrowing transition, choosing whichever panel currently contains `document.activeElement` as the selected tab *during render*, before the DOM this render produces is ever committed, with the correction synchronized into `selected` state through React's own "adjust state while rendering" pattern rather than an effect that would run after the browser may already have moved focus to `<body>` - proved by the test file's last 2 tests, which focus an element inside one panel, narrow, and assert that same element is still focused and its panel still visible.)

- [ ] **Step 7: Check types and lint**

Run: `cd src/web && npx tsc -b && npm run lint`
Expected: no output from `tsc`, and zero warnings or errors from oxlint - including the `react(set-state-in-effect)` warning the original `useEffect`-based hook produced (gone once `useSyncExternalStore` replaced it), and the `react(refs)`, `react(set-state-in-effect)`, and `react-hooks(exhaustive-deps)` warnings a `useLayoutEffect`-based version of the focus fix produced (gone once the correction moved to a plain `if` during render, over state rather than a ref, with the panels looked up by id instead of through a ref to their element).

- [ ] **Step 8: Commit**

```bash
git add src/web/src/useMediaQuery.ts src/web/src/useMediaQuery.test.ts src/web/src/WorkspacePanel.tsx src/web/src/WorkspacePanel.test.tsx src/web/src/index.css
git commit -m "feat: make the web layout responsive with an accessible workspace panel"
```

---

## Task 20: Stream the conversation, resume it, and start a new one

**Files:**
- Modify: `src/web/src/turnsApi.ts`
- Create: `src/web/src/conversationApi.ts`
- Modify: `src/web/src/TurnTracer.tsx`
- Test: `src/web/src/TurnTracer.test.tsx`

- [ ] **Step 1: Write the failing test**

Create `src/web/src/TurnTracer.test.tsx`:

```tsx
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { recordingEventStreamFactory } from './testing/fakeEventSource';
import { rememberSubmission, rememberTurnId, readInFlightTurn } from './conversationStorage';
import TurnTracer from './TurnTracer';

const CONVERSATION = 'web-conversation-1';

function stubFetch(responder: (url: string, init?: RequestInit) => Response) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) =>
    Promise.resolve(responder(String(input), init)),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function acceptedResponse(turnId: string) {
  return new Response(JSON.stringify({ turnId, alreadyAccepted: false }), {
    status: 202,
    headers: { 'Content-Type': 'application/json' },
  });
}

function renderTracer(createSource: ReturnType<typeof recordingEventStreamFactory>['factory']) {
  return render(
    <TurnTracer
      csrfToken="csrf-token"
      webConversationId={CONVERSATION}
      onTerminalOutcome={() => {}}
      createSource={createSource}
    />,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TurnTracer', () => {
  it('submits a Turn and follows its stream instead of polling', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-1/events');

    // Exactly one request: the submission. Nothing polls.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('announces progress in a live region while the answer is still being worked on', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit('accepted', { turnId: 'turn-1', receivedAt: '2026-09-04T10:00:00+00:00' }, '1');
    expect(await screen.findByRole('status')).toHaveTextContent('Accepted');

    opened[0].emit('processing', { turnId: 'turn-1', startedAt: '2026-09-04T10:00:01+00:00' }, '2');
    expect(await screen.findByRole('status')).toHaveTextContent('Working on it');
  });

  it('shows the fatal error and stops announcing progress when the open stream fails permanently', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit('processing', { turnId: 'turn-1', startedAt: '2026-09-04T10:00:01+00:00' }, '2');
    expect(await screen.findByRole('status')).toHaveTextContent('Working on it');

    opened[0].failFatally();

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Lost the connection to this Turn and cannot resume it automatically. Refresh to try again.',
    );
    expect(screen.getByRole('status')).toHaveTextContent('');
  });

  it('renders the streamed parts and the terminal Outcome together', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit(
      'part',
      { turnId: 'turn-1', order: 1, kind: 'text', text: 'One Stock Entry.', payload: null },
      '100',
    );
    opened[0].emit(
      'part',
      {
        turnId: 'turn-1',
        order: 2,
        kind: 'data',
        text: null,
        payload: {
          version: 1,
          kind: 'stock_list',
          rows: [{ id: 'entry-1', name: 'Steel Bolts', unit: 'each', location: null, note: null, quantity: '4' }],
          nextCursor: null,
          hasMore: false,
        },
      },
      '101',
    );
    opened[0].emit(
      'outcome',
      {
        turnId: 'turn-1',
        status: 'completed',
        category: 'completed',
        code: 'stock.listed',
        summary: 'One Stock Entry.',
        deliveries: [],
      },
      '1000000',
    );

    expect(await screen.findByText('stock.listed')).toBeInTheDocument();
    expect(screen.getByText('Steel Bolts')).toBeInTheDocument();
  });

  it('reconnects to a Turn it had already submitted, without submitting anything again', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-should-not-happen'));
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-1', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, 'native-1', 'turn-resumed');

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-resumed/events');

    // Reconnecting is a read. Nothing mutation-capable is resubmitted.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('resubmits the very same native message id when it never learned the Turn id', async () => {
    const fetchMock = stubFetch(() => acceptedResponse('turn-recovered'));
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    renderTracer(factory);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));

    // The same idempotency key, so the server converges on the one Turn it may already have recorded
    // rather than doing the work twice.
    expect(body.nativeMessageId).toBe('native-lost');
    await waitFor(() => expect(opened[0].url).toBe('/api/turns/turn-recovered/events'));
  });

  it('forgets the in-flight Turn once it has an answer', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(opened).toHaveLength(1));

    opened[0].emit(
      'outcome',
      { turnId: 'turn-1', status: 'completed', category: 'completed', code: 'echo', summary: 'Hi.', deliveries: [] },
      '1000000',
    );

    await waitFor(() => expect(readInFlightTurn(CONVERSATION)).toBeNull());
  });

  it('picks up a Turn another tab of the same browser profile started', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { opened, factory } = recordingEventStreamFactory();

    renderTracer(factory);
    expect(opened).toHaveLength(0);

    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-other-tab', contentText: 'list stock' });
    rememberTurnId(CONVERSATION, 'native-other-tab', 'turn-other-tab');
    window.dispatchEvent(
      new StorageEvent('storage', { key: `mca.conversation.${CONVERSATION}`, newValue: 'changed' }),
    );

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(opened[0].url).toBe('/api/turns/turn-other-tab/events');
  });

  it('never resubmits when the parent re-renders before it has learned the Turn id', async () => {
    let resolveSubmission: (response: Response) => void = () => {};
    const pending = new Promise<Response>((resolve) => {
      resolveSubmission = resolve;
    });

    const fetchMock = vi.fn(() => pending);
    vi.stubGlobal('fetch', fetchMock);

    // The one dangerous window: a stored submission whose response was never seen, so the component
    // is mid-resubmit and `turnId` is still null.
    rememberSubmission(CONVERSATION, { nativeMessageId: 'native-lost', contentText: 'list stock' });

    const { opened, factory } = recordingEventStreamFactory();
    const props = {
      csrfToken: 'csrf-token',
      webConversationId: CONVERSATION,
      onTerminalOutcome: () => {},
      createSource: factory,
    };

    const { rerender } = render(<TurnTracer {...props} />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    // A parent re-render with fresh callback identities - which any unmemoized parent produces on
    // every render - must not make this component submit mutation-capable work a second time.
    rerender(<TurnTracer {...props} onTerminalOutcome={() => {}} />);

    resolveSubmission(acceptedResponse('turn-recovered'));

    await waitFor(() => expect(opened).toHaveLength(1));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('never renders a control that would change a quantity directly', async () => {
    stubFetch(() => acceptedResponse('turn-1'));
    const { factory } = recordingEventStreamFactory();

    renderTracer(factory);

    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Send']));
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/web && npm test`
Expected: FAIL with a type error on the `webConversationId` and `createSource` props, which `TurnTracer` does not accept yet.

- [ ] **Step 3: Compose one Outcome from the streamed pieces**

Append to `src/web/src/turnsApi.ts`:

```ts
/**
 * Rebuilds the `TurnOutcomeView` shape the renderer already understands from the pieces the stream
 * delivers separately: the typed projection arrives as a data response part, and the terminal event
 * carries everything else. Keeping one shape is the point - the stream and the recovery endpoint
 * would otherwise need two renderers that could disagree about the same answer.
 */
export function composeOutcome(
  parts: { kind: 'text' | 'data'; text: string | null; payload: TurnOutcomePayload | null }[],
  terminal: {
    turnId: string;
    status: string;
    category: string;
    code: string;
    summary: string;
    deliveries: DeliveryView[];
  },
): TurnOutcomeView {
  return {
    turnId: terminal.turnId,
    status: terminal.status,
    category: terminal.category,
    code: terminal.code,
    summary: terminal.summary,
    payload: parts.find((part) => part.kind === 'data')?.payload ?? null,
    deliveries: terminal.deliveries,
  };
}
```

- [ ] **Step 4: Add the New conversation call**

Create `src/web/src/conversationApi.ts`:

```ts
/** What the server reports a conversation reset did. */
export interface ConversationRotationView {
  foundryConversationId: string;
  generation: number;
  /** True when something was waiting to be confirmed, so the Participant can be told it no longer is. */
  clearedPendingConfirmation: boolean;
}

/**
 * Starts a fresh conversation for this browser profile. The body is deliberately empty: which
 * Participant and which conversation are being reset is always trusted server-side context - the
 * signed-in session and this profile's own web conversation cookie - never anything the client says.
 */
export async function startNewConversation(csrfToken: string): Promise<ConversationRotationView> {
  const response = await fetch('/api/conversation/new', {
    method: 'POST',
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': csrfToken },
  });

  if (!response.ok) {
    throw new Error(`Starting a new conversation failed with status ${response.status}.`);
  }

  return (await response.json()) as ConversationRotationView;
}
```

- [ ] **Step 5: Stream the conversation**

In `src/web/src/TurnTracer.tsx`, replace the import block at the top of the file with:

```tsx
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  clearInFlightTurn,
  readInFlightTurn,
  rememberSubmission,
  rememberTurnId,
  subscribeToConversationChanges,
} from './conversationStorage';
import { openTurnStream, type EventStreamFactory, type TurnResponsePartEvent } from './turnStream';
import {
  composeOutcome,
  submitTurn,
  type StockChangeView,
  type StockChangesPayload,
  type StockMutationPayload,
  type StockNarrowingHints,
  type StockProposalPayload,
  type StockRowView,
  type ReferenceChangeView,
  type ReferenceChangesPayload,
  type ReferenceProposalPayload,
  type ReferenceSuggestionsPayload,
  type TurnOutcomeView,
} from './turnsApi';
```

Delete the line `const POLL_INTERVAL_MS = 1500;` - nothing polls any more.

Leave every presentational component in the file (`StockRows`, `NarrowingHints`, `StockMutationResult`, `EFFECT_LABELS`, `placementOf`, `StockChangeRows`, `StockProposal`, `StockChanges`, `ReferenceChangeRows`, `ReferenceProposal`, `ReferenceChanges`, `ReferenceSuggestions`) exactly as it is.

Replace the `TurnTracerProps` interface and the whole `function TurnTracer(...) { ... }` declaration - from `interface TurnTracerProps {` down to the closing `}` immediately before `export default TurnTracer;` - with:

```tsx
interface TurnTracerProps {
  csrfToken: string;
  /** This browser profile's stable web conversation identity, from the session bootstrap. */
  webConversationId: string;
  /** Called once a terminal Outcome arrives, so the workspace can refetch its authoritative projection. */
  onTerminalOutcome: () => void;
  /** Swapped in tests for a controllable double, since jsdom implements no EventSource. */
  createSource?: EventStreamFactory;
}

/** What this conversation is currently doing, for the live region that announces it. */
type ConversationProgress = 'idle' | 'submitting' | 'accepted' | 'processing';

const PROGRESS_TEXT: Record<Exclude<ConversationProgress, 'idle'>, string> = {
  submitting: 'Sending your message…',
  accepted: 'Accepted. Waiting for it to be picked up…',
  processing: 'Working on it…',
};

/**
 * The conversation: submits a Turn, follows its finite resumable event stream, and renders the
 * semantic parts and terminal Outcome it carries.
 *
 * It resumes rather than resubmits. On mount - after a refresh, a restart, or in a second tab - it
 * looks for this browser profile's unfinished Turn and reconnects that Turn's stream, which is a pure
 * read. Only in the one case where the browser never learned the Turn id at all does it submit again,
 * and then with the very same native message id, which the application boundary answers from the
 * Turn it already recorded rather than by doing the work twice. That is what makes reconnecting to
 * mutation-capable work safe.
 *
 * Participant and ChannelConversation identity are always derived server-side; this component never
 * supplies either, and it holds no token of any kind.
 */
function TurnTracer({ csrfToken, webConversationId, onTerminalOutcome, createSource }: TurnTracerProps) {
  const [contentText, setContentText] = useState('list stock');
  const [progress, setProgress] = useState<ConversationProgress>('idle');
  const [turnId, setTurnId] = useState<string | null>(null);
  const [parts, setParts] = useState<TurnResponsePartEvent[]>([]);
  const [outcome, setOutcome] = useState<TurnOutcomeView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const streamRef = useRef<{ close: () => void } | null>(null);
  const watchedTurnRef = useRef<string | null>(null);

  // Resuming is a once-per-mount decision, not a once-per-render one. In the window where a stored
  // submission has no Turn id yet, resuming means re-POSTing - safe, because the native message id is
  // an idempotency key, but pointless work and a second in-flight request. A parent that re-renders
  // with fresh callback identities would otherwise re-run the effect and do exactly that.
  const resumeAttemptedRef = useRef(false);

  // The parts as they arrive, mirrored outside React state so the terminal handler can compose the
  // Outcome from them without reading state inside a state updater - which React may run twice.
  const partsRef = useRef<TurnResponsePartEvent[]>([]);

  const watchTurn = useCallback(
    (id: string) => {
      if (watchedTurnRef.current === id) {
        return;
      }

      streamRef.current?.close();
      watchedTurnRef.current = id;

      partsRef.current = [];
      setTurnId(id);
      setParts([]);
      setOutcome(null);
      setProgress('accepted');

      streamRef.current = openTurnStream({
        turnId: id,
        handlers: {
          onAccepted: () => setProgress('accepted'),
          onProcessing: () => setProgress('processing'),
          onPart: (part) => {
            partsRef.current = [...partsRef.current, part];
            setParts(partsRef.current);
          },
          onOutcome: (terminal) => {
            setOutcome(composeOutcome(partsRef.current, terminal));
            setProgress('idle');
            clearInFlightTurn(webConversationId);
            onTerminalOutcome();
          },
          onFailed: () => {
            // The connection is permanently gone (a 401/403/404, or a response that was never an
            // event stream at all) rather than the transient drop the browser recovers from on its
            // own - the same error state every other failure in this component already renders, so
            // there is nothing new to build, only this one more way of reaching it.
            setError('Lost the connection to this Turn and cannot resume it automatically. Refresh to try again.');
            setProgress('idle');
          },
        },
        factory: createSource,
      });
    },
    [createSource, onTerminalOutcome, webConversationId],
  );

  const resumeStoredTurn = useCallback(async () => {
    const stored = readInFlightTurn(webConversationId);
    if (stored === null) {
      return;
    }

    if (stored.turnId !== null) {
      // A pure read. Reconnecting never resubmits.
      watchTurn(stored.turnId);
      return;
    }

    // The submission's response was never seen, so it is unknown whether the Turn exists. Sending the
    // same native message id again is the safe way to find out: the boundary is idempotent within
    // this Participant and conversation, so it either accepts it once or hands back what it recorded.
    setContentText(stored.contentText);
    setProgress('submitting');

    try {
      const result = await submitTurn(
        { nativeMessageId: stored.nativeMessageId, contentText: stored.contentText },
        csrfToken,
      );

      if (result.kind === 'outcome') {
        setTurnId(result.outcome.turnId);
        setOutcome(result.outcome);
        setProgress('idle');
        clearInFlightTurn(webConversationId);
        onTerminalOutcome();
        return;
      }

      rememberTurnId(webConversationId, stored.nativeMessageId, result.acceptance.turnId);
      watchTurn(result.acceptance.turnId);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setProgress('idle');
    }
  }, [csrfToken, onTerminalOutcome, watchTurn, webConversationId]);

  useEffect(() => {
    if (resumeAttemptedRef.current) {
      return;
    }

    resumeAttemptedRef.current = true;

    void (async () => {
      await resumeStoredTurn();
    })();
  }, [resumeStoredTurn]);

  useEffect(
    () =>
      subscribeToConversationChanges(webConversationId, () => {
        // Another tab of this browser profile submitted a Turn, or started a new conversation. Both
        // are changes to the one conversation they share, so this tab follows.
        const stored = readInFlightTurn(webConversationId);
        if (stored?.turnId != null) {
          watchTurn(stored.turnId);
        }
      }),
    [watchTurn, webConversationId],
  );

  useEffect(() => () => streamRef.current?.close(), []);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setOutcome(null);
    partsRef.current = [];
    setParts([]);
    setProgress('submitting');

    const nativeMessageId = crypto.randomUUID();

    // Recorded BEFORE the request leaves, so a response that never arrives still leaves this browser
    // profile holding the idempotency key it submitted under.
    rememberSubmission(webConversationId, { nativeMessageId, contentText });

    try {
      const result = await submitTurn({ nativeMessageId, contentText }, csrfToken);

      if (result.kind === 'outcome') {
        // This exact native message was already answered, so its recorded terminal Outcome came back
        // with the submission itself - there is nothing left to wait for.
        setTurnId(result.outcome.turnId);
        setOutcome(result.outcome);
        setProgress('idle');
        clearInFlightTurn(webConversationId);
        onTerminalOutcome();
        return;
      }

      rememberTurnId(webConversationId, nativeMessageId, result.acceptance.turnId);
      watchTurn(result.acceptance.turnId);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setProgress('idle');
    }
  }

  const streamedText = parts.filter((part) => part.kind === 'text' && part.text !== null);

  return (
    <section>
      <h2>Conversation</h2>
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
      <p>
        Confirm: <code>move stock Steel Bolts all to Shelf A</code>,{' '}
        <code>rename stock Steel Bolts to Brass Rivets</code>, <code>forget stock Steel Bolts</code>, or{' '}
        <code>change stock: add Steel Bolts quantity 2; forget Brass Rivets</code>. Anything that clears, merges, or
        forgets asks first - answer with <code>confirm &lt;code&gt;</code> or <code>reject</code>.
      </p>
      <form onSubmit={handleSubmit}>
        <label htmlFor="contentText">Message</label>
        <textarea
          id="contentText"
          value={contentText}
          onChange={(event) => setContentText(event.target.value)}
          rows={3}
        />
        <button type="submit" disabled={progress === 'submitting'}>
          Send
        </button>
      </form>

      {/*
        Announced rather than only shown: progress that a screen reader never hears is progress a
        Participant using one does not get.
      */}
      <p role="status" aria-live="polite">
        {progress === 'idle' ? '' : PROGRESS_TEXT[progress]}
      </p>

      {turnId && (
        <section>
          <h2>Turn</h2>
          <p>
            <code>{turnId}</code>
          </p>
        </section>
      )}

      {error && (
        <section role="alert">
          <h2>Error</h2>
          <p>{error}</p>
        </section>
      )}

      {streamedText.length > 0 && !outcome && (
        <section>
          <h2>Answer so far</h2>
          {streamedText.map((part) => (
            <p key={part.order}>{part.text}</p>
          ))}
        </section>
      )}

      {outcome && (
        <section>
          <h2>Result</h2>
          <dl>
            <dt>Status</dt>
            <dd>{outcome.status}</dd>
            <dt>Result</dt>
            <dd>{outcome.category}</dd>
            <dt>Code</dt>
            <dd>{outcome.code}</dd>
            <dt>Summary</dt>
            <dd>{outcome.summary}</dd>
          </dl>

          {outcome.payload?.kind === 'stock_list' && (
            <>
              <h3>Stock</h3>
              <StockRows rows={outcome.payload.rows} />
              {outcome.payload.hasMore && <p>More rows are available.</p>}
            </>
          )}

          {outcome.payload?.kind === 'stock_find' && (
            <>
              <h3>Candidates</h3>
              <StockRows rows={outcome.payload.candidates} />
              {outcome.payload.hasMoreCandidates && (
                <p>More matched than are shown here - narrow your request to see the rest.</p>
              )}
              <NarrowingHints hints={outcome.payload.narrowingHints} />
            </>
          )}

          {outcome.payload?.kind === 'stock_mutation' && <StockMutationResult payload={outcome.payload} />}

          {outcome.payload?.kind === 'stock_proposal' && (
            <StockProposal payload={outcome.payload} onCommand={setContentText} />
          )}

          {outcome.payload?.kind === 'stock_changes' && <StockChanges payload={outcome.payload} />}

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

          {outcome.deliveries.length > 0 && (
            <>
              <h3>Deliveries</h3>
              <ul>
                {outcome.deliveries.map((delivery) => (
                  <li key={delivery.deliveryId}>
                    {delivery.channel}: {delivery.status} ({delivery.attempts} attempt(s))
                  </li>
                ))}
              </ul>
            </>
          )}
        </section>
      )}
    </section>
  );
}
```

Delete the now-unused `getTurnOutcome` import if the compiler reports it; the recovery read it served is now the stream's own replay. Leave the `getTurnOutcome` function itself in `turnsApi.ts` - the disconnect-recovery HTTP contract it wraps is still part of the boundary and is still covered by integration tests.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd src/web && npm test`
Expected: PASS, 10 new tests.

- [ ] **Step 7: Check types and lint**

Run: `cd src/web && npx tsc -b && npm run lint`
Expected: no output from `tsc`, and no errors from oxlint.

- [ ] **Step 8: Commit**

```bash
git add src/web/src/TurnTracer.tsx src/web/src/TurnTracer.test.tsx src/web/src/turnsApi.ts src/web/src/conversationApi.ts
git commit -m "feat: stream the web conversation and resume it across refreshes and tabs"
```

---

## Task 21: Wire the conversation-primary application shell

**Files:**
- Modify: `src/web/src/App.tsx`
- Test: `src/web/src/App.test.tsx`

- [ ] **Step 1: Write the failing test**

Create `src/web/src/App.test.tsx`:

```tsx
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DESKTOP_WIDTH, NARROW_WIDTH, setViewportWidth } from './testing/setup';
import { FakeEventSource, installFakeEventSource } from './testing/fakeEventSource';
import { readInFlightTurn, rememberSubmission, rememberTurnId } from './conversationStorage';
import App from './App';

const BOOTSTRAP = {
  bootstrap: {
    participantId: '11111111-1111-1111-1111-111111111111',
    displayName: 'Ada Lovelace',
    webConversationId: 'web-conversation-1',
    inventories: [
      { id: 'inventory-1', shortId: 'aaaaaaaa', name: 'Main Warehouse', ownerDisplayName: 'Ada Lovelace', role: 'Editor' },
      { id: 'inventory-2', shortId: 'bbbbbbbb', name: 'Spare Warehouse', ownerDisplayName: 'Ada Lovelace', role: 'Editor' },
    ],
    activeInventoryId: 'inventory-1',
    needsOnboarding: false,
  },
  csrfToken: 'csrf-token',
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubApi(overrides: Record<string, () => Response> = {}) {
  const calls: string[] = [];

  const fetchMock = vi.fn((input: RequestInfo | URL) => {
    const url = String(input);
    calls.push(url);

    for (const [prefix, respond] of Object.entries(overrides)) {
      if (url.startsWith(prefix)) {
        return Promise.resolve(respond());
      }
    }

    if (url.startsWith('/api/session/bootstrap')) {
      return Promise.resolve(json(BOOTSTRAP));
    }

    if (url.includes('/stock')) {
      return Promise.resolve(json({ rows: [], nextCursor: null, hasMore: false }));
    }

    if (url.includes('/units')) {
      return Promise.resolve(json({ units: [], nextCursor: null, hasMore: false }));
    }

    if (url.includes('/locations')) {
      return Promise.resolve(json({ locations: [], nextCursor: null, hasMore: false }));
    }

    return Promise.resolve(json({}));
  });

  vi.stubGlobal('fetch', fetchMock);

  // The Participant-level stream opens through the default factory, so the double is installed as the
  // global EventSource. Tests then push real snapshot/changed events through it.
  const streams = installFakeEventSource();

  return { fetchMock, calls, streams };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('App', () => {
  it('keeps the conversation in the main landmark on a desktop viewport', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    stubApi();

    render(<App />);

    // Waited on deliberately. The loading state renders a `main` of its own, so waiting for `main`
    // would resolve against the loading tree and assert nothing about the ready one. The banner exists
    // only once the session bootstrap has resolved.
    await screen.findByRole('banner');

    const main = screen.getByRole('main');
    expect(within(main).getByRole('heading', { name: 'Conversation' })).toBeInTheDocument();
    expect(screen.getByRole('complementary', { name: 'Inventory workspace' })).toBeInTheDocument();
  });

  it('shows the conversation first behind a tab list on a narrow viewport', async () => {
    setViewportWidth(NARROW_WIDTH);
    stubApi();

    render(<App />);

    await screen.findByRole('banner');
    await screen.findByRole('tablist');
    const tabs = screen.getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['Conversation', 'Inventory']);
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');

    // The landmark is still there at this width; the tab panel is inside it, not instead of it.
    expect(within(screen.getByRole('main')).getByRole('tabpanel')).toBeInTheDocument();
  });

  it('shows the Active Inventory in the always-visible header at every width', async () => {
    setViewportWidth(NARROW_WIDTH);
    stubApi();

    render(<App />);

    const banner = await screen.findByRole('banner');
    expect(within(banner).getByText(/Main Warehouse/)).toBeInTheDocument();
  });

  it('switches the Active Inventory only when it is explicitly asked to', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { calls } = stubApi();

    render(<App />);
    await screen.findByRole('banner');

    // Looking at the list is browsing. Nothing has been selected.
    expect(calls.some((url) => url.includes('/select'))).toBe(false);

    await userEvent.click(await screen.findByRole('button', { name: 'Use in this conversation' }));

    await waitFor(() => expect(calls.some((url) => url === '/api/inventories/inventory-2/select')).toBe(true));
  });

  it('opens the Participant-level invalidation stream once the session is ready', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi();

    render(<App />);
    await screen.findByRole('banner');

    await waitFor(() => expect(streams.filter((s) => s.url === '/api/inventory-events')).toHaveLength(1));
  });

  it('shows a clear resync message if the Inventory stream fails permanently, and no unrelated action clears it', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi();

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    // A 401/403/404, or a response that is not `text/event-stream`, ends this way: the browser gives
    // up reconnecting on its own, so silence here would leave the workspace unable to ever invalidate
    // again until the Participant does something about it themselves.
    inventoryStreamIn(streams)!.failFatally();

    const fatalMessage =
      'Lost the connection to Inventory updates and cannot resync automatically. Refresh the page to try again.';
    expect(await screen.findByRole('alert')).toHaveTextContent(fatalMessage);

    // This warning is not an ordinary operation error - the stream is permanently dead until the page
    // is refreshed, no matter what else the Participant does in the meantime. Selecting an Inventory
    // is one of several handlers that clears the ordinary `error` state the instant it starts its own
    // attempt; it must not also erase this dedicated one, or the warning would vanish while the stream
    // stayed just as dead.
    await userEvent.click(await screen.findByRole('button', { name: 'Use in this conversation' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Use in this conversation' })).toBeInTheDocument());

    expect(screen.getByRole('alert')).toHaveTextContent(fatalMessage);
  });

  it('refetches the workspace when the stream says the Inventory version changed', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { calls, streams } = stubApi();

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(1));
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    // A change made anywhere - this conversation, another tab, another Participant, a future channel -
    // reaches this tab as a version, and the authoritative projection is re-read without a reload.
    inventoryStreamIn(streams)!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 0 }] });
    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-1', version: 1 });

    await waitFor(() => expect(calls.filter((url) => url.includes('/stock')).length).toBeGreaterThan(1));
  });

  it('does not let a locally signalled refetch swallow the next version the server publishes', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { calls, streams } = stubApi({
      '/api/turns': () => json({ turnId: 'turn-1', alreadyAccepted: false }, 202),
    });

    render(<App />);
    await screen.findByRole('banner');
    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(1));
    await waitFor(() => expect(inventoryStreamIn(streams)).toBeDefined());

    inventoryStreamIn(streams)!.emit('snapshot', { inventories: [{ inventoryId: 'inventory-1', version: 0 }] });

    // A Turn finishes. This tab knows to re-read, but the server has published no new version - so the
    // signal must live in a namespace of its own.
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(turnStreamIn(streams)).toBeDefined());
    turnStreamIn(streams)!.emit(
      'outcome',
      {
        turnId: 'turn-1',
        status: 'completed',
        category: 'completed',
        code: 'stock.listed',
        summary: 'None.',
        deliveries: [],
      },
      '1000000',
    );

    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(2));

    // Now the FIRST version the server ever publishes arrives. Had the local signal been written into
    // the server's version namespace, this would look like a version this tab had already seen, and
    // the change behind it would never be read at all.
    inventoryStreamIn(streams)!.emit('changed', { inventoryId: 'inventory-1', version: 1 });

    await waitFor(() => expect(calls.filter((url) => url.includes('/stock'))).toHaveLength(3));
  });

  it('starts a new conversation, forgets the in-flight Turn, and tells the Participant what it cleared', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    const { streams } = stubApi({
      '/api/conversation/new': () =>
        json({ foundryConversationId: 'foundry-2', generation: 2, clearedPendingConfirmation: true }),
    });

    // Something this browser profile was already waiting on. If the reset left it behind, the remounted
    // conversation would immediately reconnect - or re-POST - work from the conversation just ended.
    rememberSubmission('web-conversation-1', { nativeMessageId: 'native-old', contentText: 'list stock' });
    rememberTurnId('web-conversation-1', 'native-old', 'turn-old');

    render(<App />);
    await screen.findByRole('banner');

    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));

    const banner = screen.getByRole('banner');
    await waitFor(() =>
      expect(within(banner).getByRole('status')).toHaveTextContent(
        'Started a new conversation. The change that was waiting for confirmation was cleared.',
      ),
    );

    expect(readInFlightTurn('web-conversation-1')).toBeNull();
    expect(streamsFor(streams, '/api/turns/turn-old/events')).toHaveLength(0);
  });

  it('never offers a control that would change a quantity directly', async () => {
    setViewportWidth(DESKTOP_WIDTH);
    stubApi();

    render(<App />);
    await screen.findByRole('banner');

    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /save quantity/i })).not.toBeInTheDocument();
  });
});
```

The invalidation tests drive a real `snapshot`/`changed` pair through the fake `EventSource` installed by `stubApi`, so `App` needs no test-only code path at all. Because that double is global, this Participant's invalidation stream and any Turn stream `TurnTracer` opens land in the same list, which is why the tests pick them out by URL. Add these three helpers next to `stubApi` in the same file:

```tsx
function streamsFor(streams: FakeEventSource[], url: string) {
  return streams.filter((stream) => stream.url === url);
}

function inventoryStreamIn(streams: FakeEventSource[]) {
  return streams.find((stream) => stream.url === '/api/inventory-events');
}

function turnStreamIn(streams: FakeEventSource[]) {
  return streams.find((stream) => stream.url.startsWith('/api/turns/'));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/web && npm test`
Expected: FAIL - `screen.findByRole('main')` times out because `App` currently renders one flat `<main>` with no landmarks, no banner, and no New conversation control.

- [ ] **Step 3: Write the shell**

Replace `src/web/src/App.tsx` in full with:

```tsx
import { useCallback, useEffect, useState } from 'react';
import {
  createInventory,
  fetchBootstrap,
  selectInventory,
  MAX_INVENTORY_NAME_LENGTH,
  type BootstrapResponse,
  type InventoryView,
} from './sessionApi';
import { clearInFlightTurn } from './conversationStorage';
import { startNewConversation } from './conversationApi';
import { openInventoryStream, type InventoryVersions } from './inventoryStream';
import InitialImport from './InitialImport';
import InventoryGovernance from './InventoryGovernance';
import ReferenceWorkspace from './ReferenceWorkspace';
import StockWorkspace from './StockWorkspace';
import TurnTracer from './TurnTracer';
import WorkspacePanel from './WorkspacePanel';

type SessionState =
  | { phase: 'loading' }
  | { phase: 'unauthenticated' }
  | { phase: 'forbidden' }
  | { phase: 'ready'; session: BootstrapResponse };

/**
 * Signed-in web entry point.
 *
 * Conversation is the primary surface at every width: it is first in document order and it is what
 * `WorkspacePanel` puts in the page's `main` landmark, beside the Inventory workspace on a wide
 * viewport and in front of it on a narrow one. The workspace is a read projection and a navigation
 * surface only - browsing an Inventory there never changes which one the conversation is using, and
 * nothing in it can change a quantity.
 *
 * Projections are invalidated by the Participant-level stream rather than by guessing: whenever an
 * Inventory's version moves - because of this conversation, another tab, another Participant, or a
 * future channel - the workspace re-reads the authoritative projection. What this tab learns locally,
 * before the server has published anything, is counted separately, so a local signal can never make
 * the server's next version look like one already seen.
 */
function App() {
  const [state, setState] = useState<SessionState>({ phase: 'loading' });
  const [error, setError] = useState<string | null>(null);

  // Deliberately separate from `error`. A permanently failed Inventory stream is not an ordinary
  // operation error: the stream stays dead until the page is refreshed, no matter what the
  // Participant does next. `error` is cleared at the start of every unrelated handler (create,
  // select, new conversation) the instant it begins its own attempt; if the stream's warning lived
  // there too, any of those actions would silently erase it while the stream stayed just as dead.
  const [inventoryStreamError, setInventoryStreamError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [newInventoryName, setNewInventoryName] = useState('');
  const [creating, setCreating] = useState(false);
  const [selectingId, setSelectingId] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  const [conversationEpoch, setConversationEpoch] = useState(0);

  // Two separate namespaces on purpose. `inventoryVersions` holds ONLY what the server published;
  // `localRefetchNonce` counts the times this tab learned something locally - a Turn reaching its
  // Outcome, an import it just applied - that the server has not published a version for yet.
  // Writing a local signal into the server's namespace would be a real defect: the next version the
  // server publishes could then equal one this tab believes it has already seen, and the change
  // behind it would silently never be read.
  const [inventoryVersions, setInventoryVersions] = useState<InventoryVersions>({});
  const [localRefetchNonce, setLocalRefetchNonce] = useState(0);

  /**
   * A change this tab knows about before the server announces it. Stable across renders, because it
   * is passed to children whose effects depend on it - an identity that changed every render would
   * make them re-run for no reason, and would make a mid-flight resume look like a fresh mount.
   */
  const invalidateActiveInventory = useCallback(() => setLocalRefetchNonce((nonce) => nonce + 1), []);

  const loadSession = useCallback(async () => {
    try {
      const result = await fetchBootstrap();
      if (result.status === 'ok') {
        setState({ phase: 'ready', session: result.data });
      } else {
        setState({ phase: result.status });
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, []);

  useEffect(() => {
    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, not
    // one behind a named function reference - even though every setState call inside loadSession
    // already happens after its own internal await. Wrapping the call this way keeps loadSession
    // reusable while making that already-true post-await ordering visible to the linter too.
    void (async () => {
      await loadSession();
    })();
  }, [loadSession]);

  const isReady = state.phase === 'ready';

  useEffect(() => {
    if (!isReady) {
      return;
    }

    const stream = openInventoryStream({
      onVersions: setInventoryVersions,
      // Into the dedicated, persistent state - never into `error` - so that no unrelated handler's
      // `setError(null)` can make this warning disappear while the stream is still just as dead.
      onFailed: () =>
        setInventoryStreamError(
          'Lost the connection to Inventory updates and cannot resync automatically. Refresh the page to try again.',
        ),
    });
    return () => stream.close();
  }, [isReady]);

  async function handleCreateInventory(event: React.FormEvent) {
    event.preventDefault();
    if (state.phase !== 'ready') {
      return;
    }

    setCreating(true);
    setError(null);

    try {
      await createInventory(newInventoryName, crypto.randomUUID(), state.session.csrfToken);
      setNewInventoryName('');
      await loadSession();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setCreating(false);
    }
  }

  async function handleSelectInventory(inventory: InventoryView) {
    if (state.phase !== 'ready') {
      return;
    }

    setSelectingId(inventory.id);
    setError(null);

    try {
      const authorized = await selectInventory(inventory.id, state.session.csrfToken);
      if (!authorized) {
        setError('That Inventory is not available.');
        return;
      }

      await loadSession();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSelectingId(null);
    }
  }

  async function handleNewConversation() {
    if (state.phase !== 'ready') {
      return;
    }

    setResetting(true);
    setError(null);

    try {
      const rotation = await startNewConversation(state.session.csrfToken);

      // Forgotten BEFORE the conversation remounts, and only after the rotation succeeded. The stored
      // record belongs to the conversation that just ended: leaving it would make the remounted
      // TurnTracer immediately reconnect that Turn's stream - or, in the lost-response case, re-POST
      // it - dragging work from the old conversation into the new one on the very first render.
      clearInFlightTurn(state.session.bootstrap.webConversationId);

      // Remounts the conversation, which is what drops this tab's transcript. The Inventory the
      // Participant was working in, and every authorization they hold, are deliberately untouched -
      // starting a new conversation is not signing out.
      setConversationEpoch((epoch) => epoch + 1);
      setNotice(
        rotation.clearedPendingConfirmation
          ? 'Started a new conversation. The change that was waiting for confirmation was cleared.'
          : 'Started a new conversation.',
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setResetting(false);
    }
  }

  if (state.phase === 'loading') {
    return (
      <main>
        <h1>Multi-Channel Agent</h1>
        <p>Loading your session…</p>
      </main>
    );
  }

  if (state.phase === 'unauthenticated') {
    return (
      <main>
        <h1>Multi-Channel Agent</h1>
        <p>Sign in with your organization account to continue.</p>
        <a href="/auth/sign-in">Sign in</a>
      </main>
    );
  }

  if (state.phase === 'forbidden') {
    return (
      <main>
        <h1>Multi-Channel Agent</h1>
        <p role="alert">Your account cannot use this application right now.</p>
      </main>
    );
  }

  const { session } = state;
  const { bootstrap } = session;
  const activeInventoryId = bootstrap.activeInventoryId;
  const activeInventory = bootstrap.inventories.find((i) => i.id === activeInventoryId);

  // The Active Inventory's own published version, as the server reports it. A change made anywhere -
  // this conversation, another tab, another Participant, a future channel - moves exactly this number.
  const activeInventoryVersion = activeInventoryId ? (inventoryVersions[activeInventoryId] ?? 0) : 0;

  // One number for the projections to key on, derived from both sources. Both only ever increase, so
  // their sum increases whenever either does and can never coincidentally match a value the workspace
  // has already refetched at. Switching Inventories does not need to be handled here: every workspace
  // component's load already depends on the Inventory id it was given.
  const workspaceRefetchToken = activeInventoryVersion + localRefetchNonce;

  // One `role="alert"` node, not two: a screen reader announcing two competing alerts on the same
  // failure is not clearer, and a test asking for "the" alert should never have to disambiguate.
  // The permanent stream warning is listed first because, once it appears, it outlives whatever
  // ordinary operation error came before or after it.
  const alertMessage = [inventoryStreamError, error].filter(Boolean).join(' ');

  const conversation = (
    <>
      {/*
        The conversation is always available, including before an Inventory has been selected: that is
        exactly when a Participant needs the agent to tell them to select one, and hiding the
        conversation would make that guidance unreachable.
      */}
      <TurnTracer
        key={conversationEpoch}
        csrfToken={session.csrfToken}
        webConversationId={bootstrap.webConversationId}
        onTerminalOutcome={invalidateActiveInventory}
      />
    </>
  );

  const workspace = (
    <>
      <section>
        <h2>Your Inventories</h2>
        {bootstrap.needsOnboarding && <p>You don&apos;t belong to any Inventory yet. Create one to get started.</p>}
        {bootstrap.inventories.length === 0 && !bootstrap.needsOnboarding && <p>No Inventories yet.</p>}
        {bootstrap.inventories.length > 0 && (
          <ul>
            {bootstrap.inventories.map((inventory) => {
              const isActive = inventory.id === bootstrap.activeInventoryId;
              return (
                <li key={inventory.id}>
                  {inventory.name} — Owner: {inventory.ownerDisplayName} (#{inventory.shortId}) — {inventory.role}
                  {isActive ? (
                    <strong> (active)</strong>
                  ) : (
                    // The only thing that ever switches the conversation's Inventory. Reading the list
                    // above, or any projection below, never does.
                    <button
                      type="button"
                      onClick={() => void handleSelectInventory(inventory)}
                      disabled={selectingId === inventory.id}
                    >
                      {selectingId === inventory.id ? 'Selecting…' : 'Use in this conversation'}
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
        )}

        <form onSubmit={handleCreateInventory}>
          <label htmlFor="newInventoryName">New Inventory name</label>
          <input
            id="newInventoryName"
            value={newInventoryName}
            onChange={(event) => setNewInventoryName(event.target.value)}
            maxLength={MAX_INVENTORY_NAME_LENGTH}
            required
          />
          <button type="submit" disabled={creating || newInventoryName.trim().length === 0}>
            {creating ? 'Creating…' : 'Create Inventory'}
          </button>
        </form>
      </section>

      {activeInventory?.role === 'Owner' && (
        <InventoryGovernance
          key={activeInventory.id}
          inventoryId={activeInventory.id}
          csrfToken={session.csrfToken}
          onOwnershipChanged={() => void loadSession()}
        />
      )}

      {activeInventoryId && <StockWorkspace inventoryId={activeInventoryId} refetchToken={workspaceRefetchToken} />}

      {activeInventoryId && <ReferenceWorkspace inventoryId={activeInventoryId} refetchToken={workspaceRefetchToken} />}

      {/*
        Keyed by the Active Inventory so switching Inventories starts the workflow over rather than
        carrying a preview of one Inventory's file into another: an import proposal is bound to the
        Inventory that issued it, so none of this component's state means anything anywhere else.
      */}
      {activeInventoryId && (
        <InitialImport
          key={activeInventoryId}
          inventoryId={activeInventoryId}
          csrfToken={session.csrfToken}
          refetchToken={workspaceRefetchToken}
          onStockMayHaveChanged={invalidateActiveInventory}
        />
      )}
    </>
  );

  return (
    <>
      <header>
        <h1>Multi-Channel Agent</h1>
        <p>
          Signed in as <strong>{bootstrap.displayName}</strong>
        </p>
        {/*
          Always visible, at every width: an explicit switch that scrolled out of sight would be an
          explicit switch a Participant cannot check.
        */}
        <p>
          Active Inventory: <strong>{activeInventory ? activeInventory.name : 'none selected'}</strong>
        </p>
        <button type="button" onClick={() => void handleNewConversation()} disabled={resetting}>
          {resetting ? 'Starting…' : 'New conversation'}
        </button>
        {alertMessage && <p role="alert">{alertMessage}</p>}
        <p role="status" aria-live="polite">
          {notice ?? ''}
        </p>
      </header>

      <WorkspacePanel conversation={conversation} workspace={workspace} />
    </>
  );
}

export default App;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/web && npm test`
Expected: PASS. The whole web suite - roughly 73 tests across eight files (Task 19's `WorkspacePanel.test.tsx` grew from 10 to 15 tests and gained a sibling `useMediaQuery.test.ts` with 2 tests, both during that task's own quality-review hardening) - is green.

- [ ] **Step 5: Check types, lint, and build**

Run: `cd src/web && npx tsc -b && npm run lint && npm run build`
Expected: no output from `tsc`, no oxlint errors, and a successful Vite build.

- [ ] **Step 6: Commit**

```bash
git add src/web/src/App.tsx src/web/src/App.test.tsx
git commit -m "feat: make conversation the primary surface with version-driven workspace invalidation"
```

---

## Task 22: Every gate CI runs, run here first

**Files:**
- Modify: `README.md` (only if it documents endpoints or the web client's behaviour)

- [ ] **Step 1: Restore and build exactly as CI does**

```bash
dotnet restore
dotnet build --no-restore --configuration Release
```
Expected: `Build succeeded`, with zero warnings. `Directory.Build.props` sets `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`, so any warning is a build failure - including an unused `using` in a new file.

- [ ] **Step 2: Prove the migrations script cleanly**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations script \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --idempotent \
  --output ./migrations-check.sql
```
Expected: the script is generated with no error. Then confirm the three new migrations are in it and delete the artifact:

```bash
grep -c "AddTurnProgressEvents\|AddInventoryVersions\|AddCapturedFoundryConversationBinding" ./migrations-check.sql
rm ./migrations-check.sql
```
Expected: a count of at least 3, and no leftover file.

- [ ] **Step 3: Run the whole backend suite with Docker required**

```bash
REQUIRE_DOCKER_TESTS=true dotnet test --no-build --configuration Release --logger "console;verbosity=normal"
```
Expected: PASS, with zero skipped SQL-backed scenarios. A skip here means the SQL Server container did not come up, which CI treats as a failure - fix the environment rather than removing the variable.

- [ ] **Step 4: Run the whole web suite exactly as CI does**

```bash
cd src/web
npm ci
npm run lint
npm test
npm run build
```
Expected: no oxlint errors, all Vitest tests passing, and a successful build.

- [ ] **Step 5: Build and smoke-test the container image**

```bash
cd ../..
docker build --tag multi-channel-agent:plan-check .
```
Expected: the image builds. `DockerfileTests` already asserts every `COPY` source exists, so a build failure here means a real packaging problem.

Then run the same liveness check CI runs:

```bash
container_id=$(docker run -d -p 8080:8080 \
  -e ConnectionStrings__MultiChannelAgent="Server=placeholder;Database=placeholder;TrustServerCertificate=True" \
  -e Authentication__Provider="Entra" \
  -e Authentication__Entra__TenantId="00000000-0000-0000-0000-000000000000" \
  -e Authentication__Entra__ClientId="00000000-0000-0000-0000-000000000000" \
  -e Authentication__Entra__ClientSecret="placeholder" \
  multi-channel-agent:plan-check)
sleep 5
curl --fail --silent --output /dev/null http://localhost:8080/health/live && echo "Container is live."
docker rm -f "$container_id"
```
Expected: `Container is live.` The two new hosted workers must not stop the process from becoming live.

- [ ] **Step 6: Update the documentation this changed**

Check whether `README.md` documents the HTTP surface or the web client's behaviour:

```bash
grep -n "api/turns\|api/inventories\|polls\|polling\|conversation" README.md
```

If it does, update those passages to describe the three new routes - `GET /api/turns/{turnId}/events`, `GET /api/inventory-events`, `POST /api/conversation/new` - and to say the web client streams rather than polls. If `README.md` says nothing about either, change nothing: this ticket adds no operational knob, no configuration value, and no deployment step, so there is nothing else to document.

`CONTEXT.md` is the Inventory domain's vocabulary and gains no term here: "Turn", "ChannelConversation", and "Outcome" are Turn-workflow vocabulary that lives in code documentation, exactly as it did before this ticket.

- [ ] **Step 7: Walk the acceptance criteria against the running application**

Start the application against a local SQL Server and confirm each criterion by hand, in this order. Every one already has a test; this pass is to catch what tests structurally cannot - that the result is actually usable.

1. Widen and narrow the browser: the conversation stays primary, the workspace is reachable, and the tab list appears below 1024 px.
2. Send `list stock`: a progress line appears before the answer, and the answer arrives without the page polling (check the network panel: one open `events` request, no repeating `outcome` requests).
3. Open a second tab and reload the first mid-answer: both show the same Turn, and no second Turn appears in `InboxEntries`.
4. Add stock in one tab: the other tab's Stock workspace refreshes without being reloaded.
5. Click "Use in this conversation" on a second Inventory: the header changes. Read the first Inventory's Stock: the header does not change.
6. Ask for something that needs confirmation, then click "New conversation": the notice says the pending change was cleared, the Inventory is still active, the Inventories list is unchanged, and the code you were holding no longer works.
7. Stop the worker, send `change stock: add Steel Bolts quantity 1; add Brass Rivets quantity 2` (a batch always asks for confirmation), click "New conversation", then start the worker again: the queued Turn still answers, but its answer says the conversation it was asked in has been left behind - it offers no code at all rather than one that would be refused - and the transcript did not reappear.
8. Repeat step 7 but click "New conversation" *while the worker is running* and the Turn is mid-flight (start the worker first, then submit and immediately reset): the same thing must happen. This is the ordering only the post-dispatch re-read catches (D10), and it is the one a hand pass is actually good at provoking.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "docs: describe the streaming web conversation endpoints"
```

If Step 6 changed nothing, skip this commit rather than creating an empty one.

---

## Acceptance criteria coverage

| # | Acceptance criterion (issue #35) | Where it is built | Where it is proved |
| --- | --- | --- | --- |
| 1 | Desktop and narrow-screen layouts keep conversation primary and expose an accessible live Inventory workspace | Task 19 (`WorkspacePanel`, `useMediaQuery`, `index.css`), Task 21 (`App` shell, always-visible Active Inventory) | `WorkspacePanel.test.tsx` (landmarks, DOM order, tab semantics, arrow-key navigation, panel labelling, both panel ids always present so no tab's `aria-controls` ever dangles, conversation and workspace identity and state preserved - not remounted - across a breakpoint transition and back, media query subscription cleaned up on unmount, a focused element inside either panel stays visible and focused when narrowing even against a stale prior tab selection), `useMediaQuery.test.ts` (a stable subscription survives an unrelated rerender, a query change tears it down and starts a new one), `App.test.tsx` (conversation in `main` on desktop, conversation tab first and selected on narrow, header Active Inventory), Task 22 Step 7.1 |
| 2 | Typed Turns return stable Turn IDs and publish finite resumable SSE progress, semantic parts, and terminal Outcomes with event IDs | Task 1 (fixed sequences), Task 2 (`processing`), Task 3 (`TurnEventReader`), Task 4 (durable progress rows), Task 5 (retention), Task 6 (`GET /api/turns/{id}/events`), Task 16 (client) | `TurnStreamEventTests`, `TurnEventReaderTests` (ordering, replay, terminal, swept log, single-line data), `TurnEventStreamHttpTests` (full order with ids, resume by query and by header, ignored bad resume point, finite terminal, framing, keep-alive heartbeat), `turnStream.test.ts`, `WebConversationContinuitySqlScenarioTests` on real SQL Server. Honest scope: progress is incremental (`accepted`, then `processing`); the semantic parts are projected from the recorded Outcome and therefore arrive with the terminal event - stated in D1 and in Known limits |
| 3 | A separate Participant-level SSE stream invalidates Inventory projections after changes from any channel | Task 7 (version bump at the persistence seam, no foreign key), Task 8 (`InventoryInvalidationReader`, `GET /api/inventory-events`), Task 18 (client), Task 21 (`App` wiring) | `InventoryVersionBumpTests` (one bump per audited commit, none on denial, none on rollback, independent per Inventory, no foreign key, fallback insertion), `InventoryInvalidationReaderTests`, `InventoryEventStreamHttpTests` (snapshot, change via the conversational worker, change by another Participant over HTTP, revocation, non-disclosure, and a change made while nothing was connected arriving in the reconnect snapshot - the proof that no cursor is needed), `inventoryStream.test.ts`, `App.test.tsx` (version-driven refetch, and a local signal never swallowing the server's next version), `WebConversationContinuitySqlScenarioTests` (a genuinely concurrent pair of commits, asserted from a captured baseline) |
| 4 | One browser-profile ChannelConversation resumes across refreshes, restarts, and tabs while preserving the shared FIFO queue | Shipped `WebConversationCookie` + `SqlInboxStore` FIFO, Task 17 (`conversationStorage`), Task 20 (mount-time resume, cross-tab `storage` subscription) | `SharedBrowserProfileScenario` (one ChannelConversation across tabs, one shared FIFO order, a second tab watching the first tab's Turn, identical replay on reconnect), `conversationStorage.test.ts`, `TurnTracer.test.tsx` (resume on mount, adopt another tab's Turn), Task 22 Step 7.3 |
| 5 | Disconnect recovery retrieves recorded status and Outcome without resubmitting unknown mutation-capable work | Task 6 (the stream is a pure `GET`; disconnect cancels and undoes nothing), Task 17 (idempotency key recorded before the request leaves), Task 20 (resume reads; resubmits only the same native message id, and at most once per mount) | `TurnEventStreamHttpTests` disconnect test, `SharedBrowserProfileScenario` resubmission test (one inbox row, quantity applied exactly once), `TurnTracer.test.tsx` (reconnect issues no fetch at all; the lost-response case reuses the same `nativeMessageId`; a parent re-render during the pre-Turn-id window resubmits nothing) |
| 6 | "Use in this conversation" explicitly switches Active Inventory and records the switch; browsing never switches implicitly | Shipped `InventorySelectionService` + `POST /api/inventories/{id}/select`, Task 21 (the button is the only caller) | `SharedBrowserProfileScenario` (browsing lists, Stock, and references changes nothing; selection changes and records; a switch in one tab is every tab's switch), `App.test.tsx` (no `/select` call until the button is clicked), Task 22 Step 7.5 |
| 7 | "New conversation" rotates Foundry history and clears pending clarification/confirmation state without removing authorized access | Task 9 (generation captured at acceptance), Task 10 (`SqlConversationRotationStore`, `ProposalStatus.ConversationReset`), Task 11 (a superseded-conversation Turn leaves nothing confirmable, decided by re-reading the binding after dispatch), Task 12 (`POST /api/conversation/new`), Task 21 (the control, its notice, and forgetting the in-flight Turn) | `SqlConversationRotationStoreTests` (new generation, settled proposal, Membership and selection untouched, other conversations untouched, two rotations advancing two generations), `ConversationRotationServiceTests`, `TurnExecutionContextFactoryTests` (superseded detection and its fallback), `ConfirmationProposalLifecycleTests` (settled before dispatch; settled after dispatch by re-reading the current generation; settled when the reset lands *after* the trusted context was assembled, which is the ordering a captured flag cannot see; and the re-read waiting for a reset that is still committing rather than answering from the generation it replaces), `FoundryConversationBindingSupersessionReadTests` and `SqlFoundryConversationBindingSupersessionReadTests` (the locking read's per-provider shape, and that it never creates a binding), `SqlSupersessionReadSerializationTests` (the U &lt; P &lt; S &lt; R window on real SQL Server with `READ_COMMITTED_SNAPSHOT` on), `TurnProcessingCoordinatorTests` (a stale mutation Turn leaves nothing pending, and a reset that commits while the Turn is parked at the model boundary still leaves nothing pending), `ConversationRotationHttpTests` (generation changes, authorizations and Active Inventory survive, the held token stops working, the Initial Import proposal survives, CSRF and authentication required, work accepted before the reset still completes, and a proposal created after the reset out of work from before it can never be confirmed), `App.test.tsx` (the in-flight Turn is forgotten and no old stream reopens), `WebConversationContinuitySqlScenarioTests` (concurrent resets with a real deadlock retry, reset racing acceptance) |
| — | No monetary budget enforcement within this initial scope | Nothing is built | No task adds a cost check, spend ceiling, or budget policy; the Scope section forbids it outright |

## Parent decisions (#26) this plan upholds

| Decision | How |
| --- | --- |
| Typed progress/status events, channel-neutral response parts, one terminal Outcome; never raw tokens | `TurnEventKind` is a closed set; `TurnEventReader.ResponseParts` derives parts only from the recorded `Outcome.Summary` and `Outcome.Payload`; no task streams model tokens (D1) |
| Stable Turn/Participant/ChannelConversation/Foundry conversation identities | All four are unchanged; the stream is keyed by `TurnId`, the browser conversation by the shipped cookie, and the Foundry conversation is now captured per Turn (Task 9) |
| One web browser-profile session maps to one active Foundry generation | `FoundryConversationBindings` keeps its `(ParticipantId, ChannelConversationId)` primary key; rotation replaces the row's generation rather than adding one (Task 10), and a Turn accepted under a generation the conversation has moved past leaves nothing confirmable behind (Task 11) |
| FIFO per ChannelConversation; duplicates return the recorded outcome; processing and delivery separated | `SqlInboxStore.ClaimPendingAsync` is untouched; `SharedBrowserProfileScenario` proves both across tabs; `ITurnResultStore` keeps its atomic contract because the event log deliberately does not join it (D1). FIFO is also load-bearing for D10: it is why every Turn accepted before a reset drains before any Turn accepted after it |
| User stories 74-84 and 112 | 74/76: no mutation control in the workspace, asserted in `App.test.tsx` and `TurnTracer.test.tsx`. 75/79: Task 8 and Task 21. 77/78: Task 13 and `App.test.tsx`. 80/81/112: Tasks 3, 6, 16, 17, 20. 82: Task 13 and Task 17. 83: Tasks 10, 11, 12, 21. 84: no token ever leaves the server; Task 17 stores only a Turn id and a native message id |
| Web authentication server-side | Unchanged. The two new streams are cookie-authenticated `GET`s behind `AuthorizationPolicies.ActiveTenantMember`; `startNewConversation` sends only the CSRF header |

## Known limits, stated rather than left to be discovered

- **Semantic response parts arrive with completion, not before it.** By D1 the `part` events are projected from the recorded `Outcomes` row, which does not exist until the Turn finishes - so `part`, `part`, `outcome` all become readable in the same poll and are written back to back. What is genuinely incremental is the status: `accepted` the moment the Turn has an identity, `processing` the moment it is claimed. That satisfies AC 2 as written ("progress, semantic parts, and terminal Outcomes with event IDs"), and it is deliberately not token-by-token streaming, which #26 forbids on every channel. Making the parts incremental would mean writing a second copy of the Outcome payload - including a plaintext confirmation token with its own retention - which D1 rejects with reasons. The client is built for it either way: `TurnTracer` renders whatever parts have arrived, so a future incremental source needs no change there.
- **The per-Turn stream polls the database every 500 ms while a Turn is in flight.** That is a deliberate trade for replica-safety and a short-lived `DbContext` (D4). A Turn is in flight for seconds, and the alternative - the client's shipped 1.5-second polling - cost strictly more requests. The interval is `TurnStreamOptions.PollInterval`, so changing it is one registration; nothing else has to move.
- **The Participant-level stream re-reads the authorized set every second per connected tab.** Same trade, same single knob (`InventoryStreamOptions.PollInterval`). Membership changes being visible without a reload is worth it.
- **The Participant-level stream cannot be resumed from a position, only resynchronized.** That is the design (D5), and Task 8 proves it loses nothing. What it does mean is that a client which wants to know *what* changed while it was away cannot ask this stream; it learns only that the version moved and re-reads the projection. Nothing in #35 needs the former.
- **A `processing` marker can be lost without failing the Turn.** `TurnProcessingCoordinator` appends it outside the atomic terminal write on purpose: a courtesy must never be able to stop a Turn from reaching its answer. The consequence is that a stream may go from `accepted` straight to the answer, which is honest.
- **A Turn accepted before a reset still answers, but never with a confirmation it cannot honour.** By D10 the proposal behind such an answer is settled in the same pass, so the coordinator replaces that decision with a `conflict` / `conversation_reset` answer carrying no token, rather than recording a `confirmation_required` answer whose code is already spent. The Turn still reaches exactly one terminal Outcome, written by the same untouched atomic `ITurnResultStore.RecordAsync`; what changed is which decision is handed to it. Read-only Turns accepted before the reset are unaffected and keep their own answers.
- **Nothing confirmable survives a reset, but the guarantee is a union of two mechanisms rather than one atomic check.** D10 spells the orderings out: the post-dispatch re-read catches every rotation that reaches the binding row before it, and rotation's own transaction catches every rotation that reaches it after, because by then the Turn's proposal is already durable and pending. What makes the union total rather than probable is that the two contend for the same row lock, so there is no interleaving in which both look and neither sees anything - which is exactly why the re-read is a locking read and not the ordinary one. The overlap costs at most one no-op `InvalidatePendingAsync`. What is *not* claimed is that the proposal write and the supersession check are one transaction - they are not, and making them one would mean giving `ITurnResultStore` or `IConfirmationProposalStore` a new transactional effect this plan deliberately does not add.
- **`InboxEntries.FoundryConversationId` is nullable forever.** Only Turns accepted before Task 9's migration can be null, and the migration backfills every one that has a binding. The fallback in `TurnExecutionContextFactory` exists for the residue, is covered by its own test, and reports such a Turn as not superseded *at context-creation time* - which is correct, because it predates every reset. It is not, and must not be, an exemption from the post-dispatch check: a reset that commits while such a Turn is being processed still settles what it stored, because that check compares the re-read generation against the generation on the context rather than consulting whether the context was built from a captured binding or a fallback. Making the column non-nullable later is a separate, safe migration once no such rows remain.
- **`InventoryVersions` has no foreign key to `Inventories`.** Deliberate (D5), and it means a version row could in principle outlive an Inventory row if one were ever deleted - nothing in this system deletes one. The three mechanisms that keep the table consistent (migration backfill, save-time seeding, guarded fallback insertion) are each asserted in Task 7.
